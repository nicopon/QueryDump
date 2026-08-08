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
        
        Assert.Equal(3, info.InitSqlStatements.Length);
        Assert.Equal("INSTALL mysql;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD mysql;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'Host=localhost;Database=customers_db;User=root;' AS customers_db (TYPE MYSQL);", info.InitSqlStatements[2]);
    }

    [Fact]
    public void Parse_DuckPostgres_ReturnsHubDetails()
    {
        var conn = "duck+pg:Host=127.0.0.1;Db=sales;Password='123';";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.True(info.IsHub);
        Assert.Equal("pg", info.Provider);
        Assert.Equal("sales", info.Alias);
        Assert.Equal("INSTALL postgres;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD postgres;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'Host=127.0.0.1;Db=sales;Password=''123'';' AS sales (TYPE POSTGRES);", info.InitSqlStatements[2]);
    }

    [Fact]
    public void Parse_DuckSqlite_ReturnsHubDetails()
    {
        var conn = "duck+sqlite:data/prod.db";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.True(info.IsHub);
        Assert.Equal("sqlite", info.Provider);
        Assert.Equal("prod", info.Alias);
        Assert.Equal("INSTALL sqlite;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD sqlite;", info.InitSqlStatements[1]);
        Assert.Equal("ATTACH 'data/prod.db' AS prod (TYPE SQLITE);", info.InitSqlStatements[2]);
    }

    [Fact]
    public void Parse_DuckS3_ReturnsHttpfsWithoutAttach()
    {
        var conn = "duck+s3:s3://my-bucket/files/";
        var info = DuckHubConnectionParser.Parse(conn);

        Assert.True(info.IsHub);
        Assert.Equal("s3", info.Provider);
        Assert.Equal("s3", info.Alias);
        
        Assert.Equal(2, info.InitSqlStatements.Length);
        Assert.Equal("INSTALL httpfs;", info.InitSqlStatements[0]);
        Assert.Equal("LOAD httpfs;", info.InitSqlStatements[1]);
    }
}
