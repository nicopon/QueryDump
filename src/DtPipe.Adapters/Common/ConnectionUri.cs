using System.Text.RegularExpressions;

namespace DtPipe.Adapters.Common;

/// <summary>
/// Scheme-aware classification for connection strings. File-backed providers must not
/// claim remote URIs just because the path ends with their extension: treating
/// "s3://bucket/data.parquet" as a local file silently creates a directory literally
/// named "s3:" instead of failing closed (or going to real object storage).
/// </summary>
public static partial class ConnectionUri
{
    // Scheme = at least 2 characters ([A-Za-z][A-Za-z0-9+.-]) followed by "://".
    // Excludes Windows drive letters ("C:\data") and single-colon provider prefixes
    // ("csv:data.csv", "duck:host") which are not remote schemes.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+://")]
    private static partial Regex RemoteSchemeRegex();

    /// <summary>True when the connection string carries a remote scheme ("s3://", "azure://", "https://"…).</summary>
    public static bool HasRemoteScheme(string connectionString)
        => !string.IsNullOrWhiteSpace(connectionString) && RemoteSchemeRegex().IsMatch(connectionString);
}
