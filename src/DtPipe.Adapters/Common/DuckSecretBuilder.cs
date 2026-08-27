using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DtPipe.Adapters.Common;

/// <summary>
/// Generated SQL plus the literal secret values it embeds, so callers can strip those values
/// out of anything user-visible. DuckDB's CREATE SECRET takes literals, so the credentials do
/// exist in the statement text; they must never reach a log line or an exception message.
/// </summary>
public sealed record SecretSql(string Sql, IReadOnlyList<string> SensitiveValues)
{
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Longest first, so a value that contains another is masked as a whole.
        foreach (var value in SensitiveValues.Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v.Length))
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }
        return text;
    }
}

/// <summary>
/// Builds DuckDB <c>CREATE OR REPLACE SECRET</c> statements for object-storage locations.
///
/// Secrets are always SCOPE-bound to the container they were configured for: a single pipeline
/// can read from one bucket and write to another with different credentials, and an unscoped
/// secret would let the writer's credentials silently service the reader's bucket.
/// </summary>
public static class DuckSecretBuilder
{
    public static SecretSql BuildS3(
        ObjectUri uri,
        string? endpoint,
        string? region,
        string? accessKey,
        string? secretKey,
        string? sessionToken,
        string? urlStyle)
    {
        var parts = new List<string> { "TYPE S3" };
        var sensitive = new List<string>();

        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            parts.Add($"KEY_ID {Quote(accessKey)}");
            parts.Add($"SECRET {Quote(secretKey)}");
            sensitive.Add(accessKey);
            sensitive.Add(secretKey);

            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                parts.Add($"SESSION_TOKEN {Quote(sessionToken)}");
                sensitive.Add(sessionToken);
            }
        }
        else
        {
            // No explicit key pair: defer to DuckDB's own chain (env AWS_*, shared config,
            // instance profile). Failing here instead would break the common CI/EC2 setup.
            parts.Add("PROVIDER credential_chain");
        }

        if (!string.IsNullOrWhiteSpace(region)) parts.Add($"REGION {Quote(region)}");

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var (host, useSsl) = SplitEndpoint(endpoint);
            parts.Add($"ENDPOINT {Quote(host)}");
            if (useSsl.HasValue) parts.Add($"USE_SSL {(useSsl.Value ? "true" : "false")}");

            // S3-compatible endpoints (MinIO, Ceph, R2) are almost universally path-style, and
            // vhost-style against them fails with an opaque DNS error. Explicit flag still wins.
            parts.Add($"URL_STYLE {Quote(string.IsNullOrWhiteSpace(urlStyle) ? "path" : urlStyle)}");
        }
        else if (!string.IsNullOrWhiteSpace(urlStyle))
        {
            parts.Add($"URL_STYLE {Quote(urlStyle)}");
        }

        parts.Add($"SCOPE {Quote(uri.SecretScope)}");

        return new SecretSql(Compose("s3", uri, parts), sensitive);
    }

    public static SecretSql BuildAzure(
        ObjectUri uri,
        string? connectionString,
        string? accountName,
        string? accountKey,
        string? sasToken,
        string? endpoint)
    {
        var parts = new List<string> { "TYPE AZURE" };
        var sensitive = new List<string>();

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            parts.Add($"CONNECTION_STRING {Quote(connectionString)}");
            sensitive.Add(connectionString);
        }
        else if (!string.IsNullOrWhiteSpace(sasToken))
        {
            parts.Add("PROVIDER config");
            if (!string.IsNullOrWhiteSpace(accountName)) parts.Add($"ACCOUNT_NAME {Quote(accountName)}");
            parts.Add($"CREDENTIAL_CHAIN {Quote("none")}");
            parts.Add($"CONNECTION_STRING {Quote(BuildSasConnectionString(accountName, sasToken, endpoint))}");
            sensitive.Add(sasToken);
        }
        else if (!string.IsNullOrWhiteSpace(accountName) && !string.IsNullOrWhiteSpace(accountKey))
        {
            parts.Add($"CONNECTION_STRING {Quote(BuildKeyConnectionString(accountName, accountKey, endpoint))}");
            sensitive.Add(accountKey);
        }
        else
        {
            parts.Add("PROVIDER credential_chain");
            if (!string.IsNullOrWhiteSpace(accountName)) parts.Add($"ACCOUNT_NAME {Quote(accountName)}");
        }

        parts.Add($"SCOPE {Quote(uri.SecretScope)}");

        return new SecretSql(Compose("azure", uri, parts), sensitive);
    }

    private static string BuildKeyConnectionString(string accountName, string accountKey, string? endpoint)
    {
        var sb = new StringBuilder();
        sb.Append("DefaultEndpointsProtocol=").Append(EndpointProtocol(endpoint)).Append(';');
        sb.Append("AccountName=").Append(accountName).Append(';');
        sb.Append("AccountKey=").Append(accountKey).Append(';');
        if (!string.IsNullOrWhiteSpace(endpoint)) sb.Append("BlobEndpoint=").Append(endpoint.TrimEnd('/')).Append(';');
        return sb.ToString();
    }

    private static string BuildSasConnectionString(string? accountName, string sasToken, string? endpoint)
    {
        var sb = new StringBuilder();
        sb.Append("BlobEndpoint=");
        sb.Append(!string.IsNullOrWhiteSpace(endpoint)
            ? endpoint.TrimEnd('/')
            : $"https://{accountName}.blob.core.windows.net");
        sb.Append(';');
        sb.Append("SharedAccessSignature=").Append(sasToken.TrimStart('?')).Append(';');
        return sb.ToString();
    }

    private static string EndpointProtocol(string? endpoint) =>
        endpoint is not null && endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";

    /// <summary>
    /// Splits "http://minio:9000" into ("minio:9000", useSsl: false). DuckDB's ENDPOINT wants a
    /// bare host:port; passing the scheme through produces a confusing connection failure.
    /// Returns a null useSsl for a bare host so DuckDB's own default stands.
    /// </summary>
    private static (string Host, bool? UseSsl) SplitEndpoint(string endpoint)
    {
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return (endpoint.Substring(7).TrimEnd('/'), false);
        if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (endpoint.Substring(8).TrimEnd('/'), true);
        return (endpoint.TrimEnd('/'), null);
    }

    private static string Compose(string kind, ObjectUri uri, IEnumerable<string> parts)
        => $"CREATE OR REPLACE SECRET {SecretName(kind, uri)} ({string.Join(", ", parts)});";

    /// <summary>
    /// Deterministic, scope-derived identifier: the reader and the writer of one pipeline share a
    /// name only when they address the same container, so neither clobbers the other's secret.
    /// </summary>
    private static string SecretName(string kind, ObjectUri uri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.SecretScope));
        var suffix = string.Concat(hash.Take(4).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        return $"dtpipe_{kind}_{suffix}";
    }

    private static string Quote(string? value) => "'" + (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";
}
