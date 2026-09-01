using System.Security.Cryptography;
using System.Text;

namespace DtPipe.Sessions;

/// <summary>How a session's identity was decided — reported to the user, never guessed at twice.</summary>
public enum SessionOrigin
{
    /// <summary>--session &lt;name&gt;.</summary>
    Explicit,
    /// <summary>DTPIPE_SESSION.</summary>
    Environment,
    /// <summary>The nearest ancestor directory already holding a .dtpipe/.</summary>
    Ancestor,
    /// <summary>A new .dtpipe/ under the working directory.</summary>
    WorkingDirectory,
    /// <summary>The working directory could not be written to; user state is used instead.</summary>
    UserState
}

public sealed record SessionIdentity(string Name, string RootPath, SessionOrigin Origin);

/// <summary>
/// Resolves which session a run belongs to, by a chain of precedence rather than a single
/// mechanism — each link exists because the one below it fails for someone.
///
/// <list type="number">
/// <item><b>--session &lt;name&gt;</b> — an agent (one mission, one session), CI, deliberate isolation.</item>
/// <item><b>DTPIPE_SESSION</b> — shell scope, the ssh-agent pattern, so the flag need not be repeated.</item>
/// <item><b>The nearest ancestor .dtpipe/</b> — walking up from the working directory as git does
/// towards .git, so anywhere in a project reaches the same store.</item>
/// <item><b>A new .dtpipe/ in the working directory</b> — created only when something is actually
/// materialised. An ordinary run leaves no trace.</item>
/// <item><b>User state</b>, indexed by a hash of the absolute path — for a working directory that
/// cannot be written to.</item>
/// </list>
///
/// Rejected as a MECHANISM: printing an identifier for the user to carry from one run to the
/// next. Nobody does that twice. Kept as INFORMATION: the opt-in notice names the session, its
/// path, its retention and how to purge it.
/// </summary>
public static class SessionResolver
{
    public const string EnvironmentVariable = "DTPIPE_SESSION";
    public const string DirectoryName = ".dtpipe";
    private const string DefaultName = "default";

    /// <summary>
    /// Resolves the session for <paramref name="workingDirectory"/>. Pure: it inspects the
    /// filesystem but never creates anything. Materialising is what creates a store
    /// (<see cref="SessionStore"/>), so an ordinary run resolves and leaves no trace.
    /// </summary>
    public static SessionIdentity Resolve(string? explicitName = null, string? workingDirectory = null)
    {
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();

        if (!string.IsNullOrWhiteSpace(explicitName))
            return new SessionIdentity(Sanitize(explicitName), ProjectRoot(cwd), SessionOrigin.Explicit);

        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new SessionIdentity(Sanitize(fromEnv), ProjectRoot(cwd), SessionOrigin.Environment);

        var ancestor = FindAncestorStore(cwd);
        if (ancestor is not null)
            return new SessionIdentity(DefaultName, ancestor, SessionOrigin.Ancestor);

        if (IsWritable(cwd))
            return new SessionIdentity(DefaultName, Path.Combine(cwd, DirectoryName), SessionOrigin.WorkingDirectory);

        return new SessionIdentity(DefaultName, Path.Combine(UserStatePaths.FallbackSessionsDirectory(), PathHash(cwd)), SessionOrigin.UserState);
    }

    /// <summary>The nearest ancestor already holding a .dtpipe/, or null.</summary>
    private static string? FindAncestorStore(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DirectoryName);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string ProjectRoot(string cwd)
        => FindAncestorStore(cwd)
           ?? (IsWritable(cwd)
               ? Path.Combine(cwd, DirectoryName)
               : Path.Combine(UserStatePaths.FallbackSessionsDirectory(), PathHash(cwd)));

    private static bool IsWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".dtpipe-probe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Short, stable, collision-resistant identifier for an absolute path.</summary>
    internal static string PathHash(string absolutePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(absolutePath)));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>
    /// Keeps a session name usable as a single directory component. A name is user-supplied and
    /// becomes a path, so separators and traversal must not survive it.
    /// </summary>
    internal static string Sanitize(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
            cleaned.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');

        var result = cleaned.ToString().Trim('.', '-');
        return string.IsNullOrEmpty(result) ? DefaultName : result;
    }
}
