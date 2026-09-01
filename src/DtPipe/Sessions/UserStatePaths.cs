namespace DtPipe.Sessions;

/// <summary>
/// Where dtpipe keeps state that belongs to the user rather than to a project directory.
/// One definition, because a second one that disagreed would strand a key from its ciphertext.
/// </summary>
public static class UserStatePaths
{
    /// <summary>Overrides the state root entirely, on every platform.</summary>
    public const string RootEnvironmentVariable = "DTPIPE_STATE_HOME";

    /// <summary>
    /// The per-user state root.
    ///
    /// Linux honours XDG_STATE_HOME first: the BCL's LocalApplicationData resolves to
    /// ~/.local/share, and this is state (regenerable, machine-local), not shared data.
    /// There is no BCL constant for XDG_STATE_HOME, hence the explicit read — a small,
    /// deliberate deviation rather than a silent one. macOS and Windows have no such split
    /// and use the BCL value directly.
    /// </summary>
    public static string Root()
    {
        // An explicit override, honoured everywhere. It exists so a CI job, a container or a
        // parallel test run can own its own state root instead of sharing the user's — the
        // keys live here, and two runs racing on the same key file is a real failure, not a
        // hypothetical one.
        var explicitRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return explicitRoot;

        if (OperatingSystem.IsLinux())
        {
            var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrWhiteSpace(xdgState))
                return Path.Combine(xdgState, "dtpipe");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, ".local", "state", "dtpipe");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dtpipe");
    }

    /// <summary>Where a session's key lives — deliberately NOT next to its ciphertext.</summary>
    public static string KeysDirectory() => Path.Combine(Root(), "keys");

    /// <summary>
    /// Fallback session store for when the working directory cannot be written to (CI, a
    /// read-only mount, /tmp). Indexed by a hash of the absolute path so two such directories
    /// never share a store.
    /// </summary>
    public static string FallbackSessionsDirectory() => Path.Combine(Root(), "sessions");
}
