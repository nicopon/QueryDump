using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DtPipe.Adapters.DuckDB;

public class DuckHubConnectionInfo
{
    public bool IsHub { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string ConnectionDetails { get; init; } = string.Empty;
    public string EffectiveConnectionString { get; init; } = string.Empty;
    public string[] InitSqlStatements { get; init; } = Array.Empty<string>();
}

public static class DuckHubConnectionParser
{
    /// <summary>
    /// Closed allowlist of hub providers: a "duck+{provider}:" connection means ATTACH, so a
    /// provider only belongs here once its "ATTACH … (TYPE …)" form has been verified. An open
    /// fallback used to forward any unknown provider straight into the TYPE clause, which
    /// generated invalid SQL ("TYPE AZURE", "TYPE EXCEL") and surfaced as a raw DuckDB parse
    /// error instead of an actionable message. Maps the user-facing alias to the extension name.
    /// </summary>
    private static readonly Dictionary<string, string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pg"] = "postgres",
        ["postgres"] = "postgres",
        ["postgresql"] = "postgres",
        ["mysql"] = "mysql",
        ["sqlite"] = "sqlite",
    };

    /// <summary>
    /// Object-storage schemes that users reasonably expect to work as a hub. They never can:
    /// ATTACH integrates a relational catalog, while object storage is a transport for files.
    /// Listed only so the error can name the route that does work.
    /// </summary>
    private static readonly HashSet<string> ObjectStorageProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "s3", "s3a", "azure", "az", "gs", "gcs", "http", "https",
    };

    public static DuckHubConnectionInfo Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || !connectionString.StartsWith("duck+", StringComparison.OrdinalIgnoreCase))
        {
            return new DuckHubConnectionInfo
            {
                IsHub = false,
                EffectiveConnectionString = DuckDbConnectionHelper.GetConnectionString(connectionString)
            };
        }

        var colonIdx = connectionString.IndexOf(':');
        if (colonIdx == -1)
        {
            return new DuckHubConnectionInfo
            {
                IsHub = false,
                EffectiveConnectionString = DuckDbConnectionHelper.GetConnectionString(connectionString)
            };
        }

        var prefix = connectionString.Substring(0, colonIdx);
        var provider = prefix.Substring(5).ToLowerInvariant(); // skip "duck+"
        var connectionDetails = connectionString.Substring(colonIdx + 1);

        if (!SupportedProviders.TryGetValue(provider, out var extensionName))
        {
            throw new InvalidOperationException(BuildUnsupportedProviderMessage(provider));
        }

        var alias = GetDatabaseAlias(provider, connectionDetails);

        var typeParam = extensionName.ToUpperInvariant();
        // DuckDB ATTACH connectionDetails needs to be single-quoted.
        // Escape single quotes inside connectionDetails.
        var escapedDetails = connectionDetails.Replace("'", "''");
        var initSqls = new[]
        {
            $"INSTALL {extensionName};",
            $"LOAD {extensionName};",
            $"ATTACH '{escapedDetails}' AS {alias} (TYPE {typeParam});",
            $"USE {alias};"
        };

        return new DuckHubConnectionInfo
        {
            IsHub = true,
            Provider = provider,
            Alias = alias,
            ConnectionDetails = connectionDetails,
            EffectiveConnectionString = "Data Source=:memory:;",
            InitSqlStatements = initSqls
        };
    }

    private static string BuildUnsupportedProviderMessage(string provider)
    {
        var supported = string.Join(", ", SupportedProviders.Keys.Select(p => $"duck+{p}:"));
        var shown = provider.Length == 0 ? "(empty)" : provider;

        if (ObjectStorageProviders.Contains(provider))
        {
            return $"'duck+{provider}:' is not a hub target. The hub prefix means ATTACH, which integrates a " +
                   "relational catalog; object storage holds files, not catalogs. Read or write those locations " +
                   "through the DuckDB engine instead: " +
                   "-i duck:memory --duck-init \"INSTALL httpfs; LOAD httpfs; SET s3_region='...'\" " +
                   $"--query \"SELECT * FROM read_parquet('{provider}://bucket/key.parquet')\", or COPY ... TO the " +
                   $"same URI via --post-exec. Supported hub providers: {supported}.";
        }

        return $"Unknown DuckDB hub provider '{shown}'. Supported hub providers: {supported}. " +
               "Other DuckDB extensions are reachable with --duck-init (INSTALL/LOAD) plus --query.";
    }

    private static string GetDatabaseAlias(string provider, string connectionDetails)
    {
        // Try to find Database=, DbName=, or Db= in connection details (supports semicolon or space delimited)
        var match = Regex.Match(connectionDetails, @"\b(?:Database|DbName|Db)\s*[=:]\s*([^;\s]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var dbName = match.Groups[1].Value.Trim('\'', '"', ' ');
            if (dbName.Length > 0)
            {
                // Ensure the alias is a valid SQL identifier (alphanumeric and underscores only)
                var safeName = Regex.Replace(dbName, @"[^a-zA-Z0-9_]", "_");
                if (safeName.Length > 0) return safeName;
            }
        }

        // Try to get filename for sqlite
        if (provider == "sqlite")
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(connectionDetails);
                if (!string.IsNullOrEmpty(fileName))
                {
                    var safeName = Regex.Replace(fileName, @"[^a-zA-Z0-9_]", "_");
                    if (safeName.Length > 0) return safeName;
                }
            }
            catch { }
        }

        return provider;
    }
}
