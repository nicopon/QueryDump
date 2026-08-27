using DtPipe.Adapters.DuckDB;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.DuckDB;

/// <summary>
/// GetConnectionString only ever sees selector-stripped values: ComponentSelector removes "duck:"
/// / "duck+mysql:" before any descriptor is called. Earlier tests drove it with the prefix still
/// attached, which exercised a branch the CLI never took — so "duck:memory" kept creating a
/// database FILE named "memory" while the suite stayed green. These inputs mirror the runtime.
/// </summary>
public class DuckDbConnectionHelperTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData(":memory:")]
    [InlineData(":memory")]
    [InlineData("")]
    public void InMemory_Spellings_Map_To_The_Memory_Sentinel(string connectionString)
        => Assert.Equal("Data Source=:memory:;", DuckDbConnectionHelper.GetConnectionString(connectionString));

    [Theory]
    [InlineData("warehouse.duckdb", "Data Source=warehouse.duckdb;")]
    [InlineData("/tmp/data/w.duckdb", "Data Source=/tmp/data/w.duckdb;")]
    [InlineData(" spaced.duckdb ", "Data Source=spaced.duckdb;")]
    public void File_Paths_Are_Preserved(string connectionString, string expected)
        => Assert.Equal(expected, DuckDbConnectionHelper.GetConnectionString(connectionString));

    [Fact]
    public void An_Explicit_Ado_Connection_String_Is_Passed_Through()
        => Assert.Equal("Data Source=x.duckdb;", DuckDbConnectionHelper.GetConnectionString("Data Source=x.duckdb;"));
}
