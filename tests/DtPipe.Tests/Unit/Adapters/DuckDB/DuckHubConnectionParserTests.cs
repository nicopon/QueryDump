using System;
using DtPipe.Adapters.DuckDB;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.DuckDB;

public class DuckHubConnectionParserTests
{
    [Fact]
    public void Parse_NormalConnectionString_ReturnsNonHub()
    {
        var conn = "mydb.duckdb";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.False(info.IsHub);
        Assert.Equal("Data Source=mydb.duckdb;", info.EffectiveConnectionString);
    }

    [Fact]
    public void Parse_DuckMySql_ReturnsHubDetails()
    {
        var conn = "duck+mysql:Host=localhost;Database=customers_db;User=root;";
        var info = DuckHubConnectionParser.Parse(conn);

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

    [Fact]
    public void Parse_DuckPostgres_ReturnsHubDetails()
    {
        var conn = "duck+pg:Host=127.0.0.1;Db=sales;Password='123';";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.True(info.IsHub);
        Assert.Equal("pg", info.Provider);
        Assert.Equal("sales", info.Alias);
        Assert.Equal(4, info.InitSqlStatements.Length);
        Assert.Equal("INSTALL postgres;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD postgres;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'Host=127.0.0.1;Db=sales;Password=''123'';' AS sales (TYPE POSTGRES);", info.InitSqlStatements[2]);
        Assert.Equal("USE sales;", info.InitSqlStatements[3]);
    }

    [Fact]
    public void Parse_DuckSqlite_ReturnsHubDetails()
    {
        var conn = "duck+sqlite:data/prod.db";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.True(info.IsHub);
        Assert.Equal("sqlite", info.Provider);
        Assert.Equal("prod", info.Alias);
        Assert.Equal(4, info.InitSqlStatements.Length);
        Assert.Equal("INSTALL sqlite;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD sqlite;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'data/prod.db' AS prod (TYPE SQLITE);", info.InitSqlStatements[2]);
        Assert.Equal("USE prod;", info.InitSqlStatements[3]);
    }

    /// <summary>
    /// Object storage is a transport for files, not a relational catalog, so it can never be an
    /// ATTACH target. "duck+s3:" used to emit INSTALL/LOAD httpfs and silently DROP the URI,
    /// forcing the user to repeat it inside --query and leaving writes pointed at a catalog that
    /// does not exist. Failing closed with the working route named is the contract now.
    /// </summary>
    [Theory]
    [InlineData("duck+s3:s3://my-bucket/files/")]
    [InlineData("duck+azure:container/blob.parquet")]
    [InlineData("duck+az:container/blob.parquet")]
    [InlineData("duck+gs:bucket/key.parquet")]
    [InlineData("duck+https://example.com/feed.jsonl")]
    public void Parse_ObjectStorageProvider_Throws_And_Names_The_Working_Route(string conn)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DuckHubConnectionParser.Parse(conn));

        Assert.Contains("not a hub target", ex.Message);
        Assert.Contains("--duck-init", ex.Message);
        Assert.Contains("duck+sqlite:", ex.Message);
    }

    /// <summary>
    /// The open "_ => provider" fallback forwarded any unknown provider into the TYPE clause,
    /// producing invalid SQL such as "ATTACH ... (TYPE EXCEL)". Unknown providers must fail with
    /// the supported list instead of a raw DuckDB parse error.
    /// </summary>
    [Theory]
    [InlineData("duck+excel:data.xlsx")]
    [InlineData("duck+mssql:Server=localhost;Database=db;")]
    [InlineData("duck+bigquery:project=p;")]
    [InlineData("duck+nonsense:whatever")]
    [InlineData("duck+:whatever")]
    public void Parse_UnknownProvider_Throws_With_Supported_List(string conn)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DuckHubConnectionParser.Parse(conn));

        Assert.Contains("Unknown DuckDB hub provider", ex.Message);
        Assert.Contains("duck+pg:", ex.Message);
        Assert.Contains("duck+mysql:", ex.Message);
        Assert.Contains("duck+sqlite:", ex.Message);
    }

    /// <summary>Plain "duck:" connections and file paths keep bypassing the hub entirely.</summary>
    [Theory]
    [InlineData("duck:memory")]
    [InlineData("duck::memory:")]
    [InlineData("data/warehouse.duckdb")]
    public void Parse_NonHubConnection_IsNotAffected(string conn)
    {
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.False(info.IsHub);
        Assert.Empty(info.InitSqlStatements);
    }
}
