using System.Security.Cryptography;

namespace DtPipe.Sessions;

/// <summary>
/// Holds one AES key per session, in user state — <b>deliberately not beside the ciphertext</b>.
///
/// What that separation buys, and it is not confidentiality at rest:
///
/// <list type="bullet">
/// <item><b>An inert copy.</b> The project directory can be rsynced, backed up, synced to a
/// cloud folder, archived or `git add -A`-ed; the ciphertext travels without the key and says
/// nothing.</item>
/// <item><b>A reliable purge.</b> Deleting the key makes every artefact unreadable at once, even
/// if removing the files then fails halfway. Crypto-shredding, not tidying.</item>
/// </list>
///
/// What it does not buy: protection from someone with access to the account — they have the key
/// too. The disk is usually already encrypted (FileVault, BitLocker) and that is the layer doing
/// that job.
///
/// The OS keyring is deliberately NOT used. It is for what a user chose to keep
/// (<c>dtpipe secret</c>), not for what the tool makes and destroys: an unsigned binary triggers
/// a keychain prompt per access on macOS, and making a checkpoint read depend on an unlocked
/// keyring breaks CI outright.
/// </summary>
public static class SessionKeyStore
{
    private const int KeySizeBytes = 32; // AES-256

    public static string KeyPath(string sessionName)
        => Path.Combine(UserStatePaths.KeysDirectory(), $"{SessionResolver.Sanitize(sessionName)}.key");

    /// <summary>
    /// Returns this session's key, creating it on first use.
    /// </summary>
    /// <exception cref="IOException">
    /// If the key directory cannot be written to. This fails the run on purpose: falling back to
    /// writing artefacts in the clear would silently void both properties above, for the whole
    /// store and retroactively — every other session in it included.
    /// </exception>
    public static byte[] GetOrCreateKey(string sessionName)
    {
        var path = KeyPath(sessionName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == KeySizeBytes) return existing;
            // A truncated key can only produce unreadable artefacts; replace it rather than
            // failing every future read with a decryption error.
            File.Delete(path);
        }

        var dir = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(dir);
            var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            File.WriteAllBytes(path, key);
            RestrictToOwner(path);
            return key;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException(
                $"dtpipe could not write the session key to '{dir}'. Materialisation is refused rather " +
                $"than writing artefacts unencrypted — the guarantee is a property of the whole store, " +
                $"so one cleartext session would void it for all of them. Original error: {ex.Message}", ex);
        }
    }

    /// <summary>Returns the key if it exists, or null. Never creates one.</summary>
    public static byte[]? TryGetKey(string sessionName)
    {
        var path = KeyPath(sessionName);
        if (!File.Exists(path)) return null;
        var key = File.ReadAllBytes(path);
        return key.Length == KeySizeBytes ? key : null;
    }

    /// <summary>Destroys a session's key. This is what makes its artefacts unreadable.</summary>
    public static void DeleteKey(string sessionName)
    {
        var path = KeyPath(sessionName);
        if (File.Exists(path)) File.Delete(path);
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // NTFS inherits the user profile's ACL.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
