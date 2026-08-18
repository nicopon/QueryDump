using System;
using System.IO;
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

        var extensionName = provider switch
        {
            "pg" or "postgres" or "postgresql" => "postgres",
            "mysql" => "mysql",
            "sqlite" => "sqlite",
            "s3" or "http" or "https" => "httpfs",
            _ => provider
        };

        var alias = GetDatabaseAlias(provider, connectionDetails);

        string[] initSqls;
        if (extensionName == "httpfs")
        {
            initSqls = new[]
            {
                "INSTALL httpfs;",
                "LOAD httpfs;"
            };
        }
        else
        {
            var typeParam = extensionName.ToUpperInvariant();
            // DuckDB ATTACH connectionDetails needs to be single-quoted.
            // Escape single quotes inside connectionDetails.
            var escapedDetails = connectionDetails.Replace("'", "''");
            initSqls = new[]
            {
                $"INSTALL {extensionName};",
                $"LOAD {extensionName};",
                $"ATTACH '{escapedDetails}' AS {alias} (TYPE {typeParam});",
                $"USE {alias};"
            };
        }

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
