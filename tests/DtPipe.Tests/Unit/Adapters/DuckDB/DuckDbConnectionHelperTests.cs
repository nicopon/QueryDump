using DtPipe.Adapters.DuckDB;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.DuckDB;

public class DuckDbConnectionHelperTests
{
    /// <summary>
    /// Both documented in-memory spellings used to produce a database FILE: the leading-colon
    /// strip turned "duck::memory:" into "memory:" and "duck:memory" was passed through as
    /// "memory", so runs quietly created a file named "memory" in the working directory.
    /// </summary>
    [Theory]
    [InlineData("duck::memory:")]
    [InlineData("duck:memory")]
    [InlineData("duck:")]
    [InlineData("duck::memory")]
    [InlineData("")]
    public void InMemory_Spellings_Map_To_The_Memory_Sentinel(string connectionString)
        => Assert.Equal("Data Source=:memory:;", DuckDbConnectionHelper.GetConnectionString(connectionString));

    [Theory]
    [InlineData("duck:warehouse.duckdb", "Data Source=warehouse.duckdb;")]
    [InlineData("duck:/tmp/data/w.duckdb", "Data Source=/tmp/data/w.duckdb;")]
    [InlineData("warehouse.duckdb", "Data Source=warehouse.duckdb;")]
    public void File_Paths_Are_Preserved(string connectionString, string expected)
        => Assert.Equal(expected, DuckDbConnectionHelper.GetConnectionString(connectionString));

    [Fact]
    public void An_Explicit_Ado_Connection_String_Is_Passed_Through()
        => Assert.Equal("Data Source=x.duckdb;", DuckDbConnectionHelper.GetConnectionString("Data Source=x.duckdb;"));
}
