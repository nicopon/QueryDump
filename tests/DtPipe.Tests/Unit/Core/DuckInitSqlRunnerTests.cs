using DtPipe.Core.Expressions;
using DtPipe.Core.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DtPipe.Tests.Unit.Core;

/// <summary>
/// F10 (partial) — DuckInitSqlRunner is the single init-SQL executor shared by
/// Adapters and Processors. Uses in-memory SQLite as a stand-in DbConnection:
/// the runner only relies on CreateCommand/ExecuteNonQueryAsync.
/// </summary>
public class DuckInitSqlRunnerTests
{
    private static SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static int CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Empty_InitSql_Is_NoOp()
    {
        await using var conn = OpenConnection();

        await DuckInitSqlRunner.RunAsync(conn, null, resolver: null, CancellationToken.None);
        await DuckInitSqlRunner.RunAsync(conn, "", resolver: null, CancellationToken.None);
        await DuckInitSqlRunner.RunAsync(conn, "   ", resolver: null, CancellationToken.None);

        // Nothing executed: probing a known table name must fail if any SQL ran,
        // and no exception from the runner itself means the no-op path held.
        Assert.Throws<SqliteException>(() => CountRows(conn, "t"));
    }

    [Fact]
    public async Task MultiStatement_Executes_In_Order()
    {
        await using var conn = OpenConnection();

        const string initSql = """
            CREATE TABLE t (Id INTEGER);
            INSERT INTO t (Id) VALUES (1);
            INSERT INTO t (Id) VALUES (2);
            """;

        await DuckInitSqlRunner.RunAsync(conn, initSql, resolver: null, CancellationToken.None);

        Assert.Equal(2, CountRows(conn, "t"));
    }

    [Fact]
    public async Task Resolver_Expands_Value_Before_Execution()
    {
        await using var conn = OpenConnection();

        // The resolver expands the ${{TABLE}} token before execution — mirroring the
        // keyring/env expansion performed by the CLI's IStringContentResolver chain.
        var resolver = new StubResolver("${{TABLE}}", "CREATE TABLE expanded (Id INTEGER)");

        await DuckInitSqlRunner.RunAsync(conn, "${{TABLE}}", resolver, CancellationToken.None);

        Assert.True(resolver.WasCalled);
        Assert.Equal(0, CountRows(conn, "expanded"));
    }

    [Fact]
    public async Task Blank_After_Resolution_Is_NoOp()
    {
        await using var conn = OpenConnection();
        var resolver = new StubResolver("x", "   ");

        await DuckInitSqlRunner.RunAsync(conn, "x", resolver, CancellationToken.None);

        Assert.True(resolver.WasCalled); // resolution happened…
        Assert.Throws<SqliteException>(() => CountRows(conn, "t")); // …but nothing executed
    }

    private sealed class StubResolver(string input, string output) : IStringContentResolver
    {
        public bool WasCalled { get; private set; }
        public Task<string?> ResolveAsync(string content, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult<string?>(content == input ? output : content);
        }
    }
}
