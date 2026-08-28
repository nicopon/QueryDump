using System;
using DtPipe.Adapters.DuckDB;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.DuckDB;

/// <summary>
/// The parser no longer sees connection-string prefixes: ComponentSelector splits
/// "duck+mysql:Host=…" into variant "mysql" and details "Host=…" before any adapter is reached.
/// These tests therefore drive it the way the runtime does — (variant, details) — and the
/// selector grammar itself is covered by ComponentSelectorTests.
/// <para>
/// The hub allowlist is empty: a hub route only ever covers a database with no native provider,
/// and every attachable one now has it. The prefix exists solely to fail with an actionable
/// message naming the native route, instead of an obscure DuckDB parse error.
/// </para>
/// </summary>
public class DuckHubConnectionParserTests
{
    [Fact]
    public void Parse_NoVariant_ReturnsNonHub()
    {
        var info = DuckHubConnectionParser.Parse(null, "mydb.duckdb");

        Assert.False(info.IsHub);
        Assert.Equal("Data Source=mydb.duckdb;", info.EffectiveConnectionString);
    }

    /// <summary>
    /// A refused database must get advice, not just a refusal — but the message names no provider
    /// prefix on purpose. Doing so would put a copy of the component catalog inside this DuckDB
    /// component, unverifiable and stale the day a provider is renamed; it points at
    /// "dtpipe providers", the live list, instead.
    /// </summary>
    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("pg")]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    [InlineData("mssql")]
    [InlineData("oracle")]
    public void Parse_DatabaseVariant_Throws_And_Points_At_The_Live_Provider_List(string variant)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse(variant, "Host=127.0.0.1;Database=sales;"));

        Assert.Contains("not a supported connection", ex.Message);
        Assert.Contains("use its own prefix", ex.Message);
        Assert.Contains("dtpipe providers", ex.Message);
    }

    /// <summary>
    /// Object storage is a transport for files, not a relational catalog, so it can never be an
    /// ATTACH target. Failing closed with the working route named is the contract.
    /// </summary>
    [Theory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("az")]
    [InlineData("gs")]
    [InlineData("https")]
    public void Parse_ObjectStorageVariant_Throws_And_Names_The_Working_Route(string variant)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse(variant, "bucket/key.parquet"));

        Assert.Contains("not a hub target", ex.Message);
        Assert.Contains("--duck-init", ex.Message);
    }

    /// <summary>
    /// The open "_ => provider" fallback forwarded any unknown provider into the TYPE clause,
    /// producing invalid SQL such as "ATTACH ... (TYPE EXCEL)".
    /// </summary>
    [Theory]
    [InlineData("excel")]
    [InlineData("bigquery")]
    [InlineData("nonsense")]
    public void Parse_UnknownVariant_Throws_And_Points_At_DuckInit(string variant)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse(variant, "whatever"));

        Assert.Contains("not a supported connection", ex.Message);
        Assert.Contains("--duck-init", ex.Message);
    }

    /// <summary>
    /// Pins the empty allowlist. Re-adding a hub provider must be a deliberate edit that also
    /// revisits this test, not something that slips in because nothing asserted the state.
    /// </summary>
    [Fact]
    public void No_Variant_Is_Accepted_As_A_Hub_Target()
    {
        foreach (var variant in new[] { "mysql", "pg", "sqlite", "mssql", "oracle", "duckdb", "excel", "s3" })
        {
            Assert.Throws<InvalidOperationException>(
                () => DuckHubConnectionParser.Parse(variant, "Host=localhost;Database=db;"));
        }
    }

    /// <summary>Plain DuckDB targets carry no variant and must bypass the hub entirely.</summary>
    [Theory]
    [InlineData("memory")]
    [InlineData(":memory:")]
    [InlineData("data/warehouse.duckdb")]
    public void Parse_NonHubConnection_IsNotAffected(string details)
    {
        var info = DuckHubConnectionParser.Parse(null, details);

        Assert.False(info.IsHub);
        Assert.Empty(info.InitSqlStatements);
    }
}
