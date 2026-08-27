using System;
using System.Collections.Generic;
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
    /// Postgres and SQLite are deliberately absent: DtPipe already has native providers for both
    /// ("pg:"/"postgres:", "sqlite:") with COPY/bulk/upsert support that ATTACH cannot reach, so
    /// the hub route is strictly inferior there. MySQL stays because no native provider exists yet.
    /// </summary>
    private static readonly Dictionary<string, string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mysql"] = "mysql",
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

    /// <summary>
    /// Builds the ATTACH plan for a hub connection.
    /// <para>
    /// Takes the variant and the connection details separately: <c>ComponentSelector</c> has already
    /// split "duck+mysql:Host=…" into variant "mysql" and details "Host=…". This method used to
    /// re-parse the "duck+" prefix itself, which is exactly the prefix knowledge an adapter is not
    /// supposed to carry.
    /// </para>
    /// </summary>
    /// <param name="variant">The selector variant ("mysql"), or null/empty for a non-hub connection.</param>
    /// <param name="connectionDetails">The selector-stripped connection string.</param>
    public static DuckHubConnectionInfo Parse(string? variant, string connectionDetails)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return new DuckHubConnectionInfo
            {
                IsHub = false,
                EffectiveConnectionString = DuckDbConnectionHelper.GetConnectionString(connectionDetails)
            };
        }

        var provider = variant.Trim().ToLowerInvariant();

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
            EffectiveConnectionString = DuckDbConnectionHelper.InMemoryConnectionString,
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

        // Falling back to the bare provider name let two ATTACHes without an explicit database
        // (e.g. one input, one output) collide on the same alias and silently USE the wrong
        // catalog. Fail closed instead of guessing.
        throw new InvalidOperationException(
            $"'duck+{provider}:' connection string must specify a database name (Database=, DbName=, or Db=) " +
            "so the attached catalog gets a unique, unambiguous alias.");
    }
}
