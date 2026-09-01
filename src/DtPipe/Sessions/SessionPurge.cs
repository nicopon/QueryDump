using System.Text.Json;

namespace DtPipe.Sessions;

/// <summary>
/// Removes sessions whose TTL has run out.
///
/// Two properties, both deliberate:
///
/// <b>Silent and unasked.</b> Housekeeping that stops to ask a question is housekeeping nobody
/// runs. Expiry is a decision already taken when the session was created; the purge only
/// carries it out.
///
/// <b>The key goes first.</b> Deleting the key makes everything it protected unreadable
/// immediately, so a half-completed directory removal leaves inert bytes rather than readable
/// data. That ordering is the whole reason the ciphertext and the key live in different places
/// — it is what makes the purge reliable rather than best-effort.
/// </summary>
public static class SessionPurge
{
    /// <summary>
    /// Purges expired sessions under <paramref name="rootPath"/>. Never throws: a purge that
    /// fails must not fail the run it was housekeeping for.
    /// </summary>
    /// <returns>The names of the sessions removed.</returns>
    public static IReadOnlyList<string> PurgeExpired(string rootPath, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var removed = new List<string>();

        foreach (var sessionPath in SessionStore.EnumerateSessionPaths(rootPath))
        {
            try
            {
                if (!IsExpired(sessionPath, now)) continue;
                Remove(Path.GetFileName(sessionPath), sessionPath);
                removed.Add(Path.GetFileName(sessionPath));
            }
            catch
            {
                // Skip this one and carry on: one unreadable session must not strand the rest.
            }
        }

        return removed;
    }

    /// <summary>Removes one session: its key first, then its files.</summary>
    public static void Remove(string sessionName, string sessionPath)
    {
        SessionKeyStore.DeleteKey(sessionName);
        if (Directory.Exists(sessionPath)) Directory.Delete(sessionPath, recursive: true);
    }

    private static bool IsExpired(string sessionPath, DateTime utcNow)
    {
        var metadataPath = Path.Combine(sessionPath, "session.json");
        if (!File.Exists(metadataPath))
        {
            // No metadata: fall back to the directory's own timestamp rather than keeping an
            // orphan forever, and rather than deleting something that may be in use.
            return Directory.GetLastWriteTimeUtc(sessionPath).AddDays(SessionStore.DefaultTtlDays) < utcNow;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = doc.RootElement;

            var ttlDays = root.TryGetProperty("ttl_days", out var ttlEl) && ttlEl.TryGetInt32(out var t)
                ? t
                : SessionStore.DefaultTtlDays;

            var lastUsed = root.TryGetProperty("last_used_at", out var luEl) && luEl.GetString() is { } lu
                       && DateTime.TryParse(lu, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : Directory.GetLastWriteTimeUtc(sessionPath);

            return lastUsed.AddDays(ttlDays) < utcNow;
        }
        catch
        {
            return false;
        }
    }
}
