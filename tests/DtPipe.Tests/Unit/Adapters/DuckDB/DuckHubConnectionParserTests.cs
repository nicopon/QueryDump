using System;
using DtPipe.Adapters.DuckDB;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.DuckDB;

/// <summary>
/// The parser no longer sees connection-string prefixes: ComponentSelector splits
/// "duck+mysql:Host=…" into variant "mysql" and details "Host=…" before any adapter is reached.
/// These tests therefore drive it the way the runtime does — (variant, details) — and the
/// selector grammar itself is covered by ComponentSelectorTests.
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

    [Fact]
    public void Parse_MySqlVariant_ReturnsHubDetails()
    {
        var info = DuckHubConnectionParser.Parse("mysql", "Host=localhost;Database=customers_db;User=root;");

        Assert.True(info.IsHub);
        Assert.Equal("mysql", info.Provider);
        Assert.Equal("customers_db", info.Alias);
        Assert.Equal("Host=localhost;Database=customers_db;User=root;", info.ConnectionDetails);
        Assert.Equal("Data Source=:memory:;", info.EffectiveConnectionString);

        Assert.Equal(4, info.InitSqlStatements.Length);
        Assert.Equal("INSTALL mysql;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD mysql;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'Host=localhost;Database=customers_db;User=root;' AS customers_db (TYPE MYSQL);", info.InitSqlStatements[2]);
        Assert.Equal("USE customers_db;", info.InitSqlStatements[3]);
    }

    /// <summary>
    /// Falling back to the bare provider name when no Database=/DbName=/Db= is present let two
    /// ATTACHes in the same process (e.g. one input, one output) collide on the same alias and
    /// silently USE the wrong catalog. The alias must be derived from an explicit database name,
    /// or parsing fails closed.
    /// </summary>
    [Fact]
    public void Parse_MySqlVariant_WithoutDatabaseName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse("mysql", "Host=localhost;User=root;"));

        Assert.Contains("must specify a database name", ex.Message);
        Assert.Contains("duck+mysql:", ex.Message);
    }

    /// <summary>
    /// Postgres and SQLite are deliberately not hub variants: DtPipe already has native providers
    /// for both ("pg:"/"postgres:", "sqlite:") with COPY/bulk/upsert support that ATTACH cannot
    /// reach, so routing them through the hub would be strictly inferior.
    /// </summary>
    [Theory]
    [InlineData("pg")]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData("sqlite")]
    public void Parse_NativelySupportedVariant_Throws_With_Supported_List(string variant)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse(variant, "Host=127.0.0.1;Database=sales;"));

        Assert.Contains("Unknown DuckDB hub provider", ex.Message);
        Assert.Contains("duck+mysql:", ex.Message);
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
        Assert.Contains("duck+mysql:", ex.Message);
    }

    /// <summary>
    /// The open "_ => provider" fallback forwarded any unknown provider into the TYPE clause,
    /// producing invalid SQL such as "ATTACH ... (TYPE EXCEL)".
    /// </summary>
    [Theory]
    [InlineData("excel")]
    [InlineData("mssql")]
    [InlineData("bigquery")]
    [InlineData("nonsense")]
    public void Parse_UnknownVariant_Throws_With_Supported_List(string variant)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DuckHubConnectionParser.Parse(variant, "whatever"));

        Assert.Contains("Unknown DuckDB hub provider", ex.Message);
        Assert.Contains("duck+mysql:", ex.Message);
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
