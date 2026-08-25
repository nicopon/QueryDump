using System.Data.Common;
using DtPipe.Core.Expressions;

namespace DtPipe.Core.Security;

/// <summary>
/// Single implementation of the <c>--duck-init</c> SQL executor, shared by the DuckDB
/// adapter (DtPipe.Adapters) and the DuckDB SQL stream processor (DtPipe.Processors).
///
/// Resolves the init SQL through <see cref="IStringContentResolver"/> (env / keyring /
/// file expansion) and executes it as a single command — DuckDB natively handles
/// multi-statement command text, so no client-side statement splitting is performed.
/// </summary>
public static class DuckInitSqlRunner
{
    public static async Task RunAsync(
        DbConnection connection,
        string? initSql,
        IStringContentResolver? resolver,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(initSql)) return;

        var sql = await (resolver ?? DefaultStringContentResolver.Instance).ResolveAsync(initSql, ct);
        if (string.IsNullOrWhiteSpace(sql)) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
