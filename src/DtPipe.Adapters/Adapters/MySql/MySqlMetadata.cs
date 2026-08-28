namespace DtPipe.Adapters.MySql;

internal static class MySqlMetadata
{
    public const string ComponentName = "mysql";

    /// <summary>
    /// Returns false because there is no deterministic way to ensure a connection string
    /// belongs to this provider without a prefix. We group this under an explicit choice
    /// to avoid ambiguity.
    /// <para>
    /// Not a placeholder: a MySQL connection string opens on "Server=…;Database=…", which is
    /// character-for-character what a SQL Server one looks like. Any content heuristic here would
    /// claim connections belonging to the other provider. The "mysql:" selector is required, and
    /// <c>ComponentSelector</c> — not this method — is what recognizes it.
    /// </para>
    /// </summary>
    public static bool CanHandle(string connectionString) => false;

    public const bool SupportsStdio = false;
}
