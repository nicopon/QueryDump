using Xunit;

namespace DtPipe.Tests.Unit.Core;

/// <summary>
/// F10 — Core boundary deny-list. Concrete infrastructure types must NOT live in the
/// DtPipe.Core assembly: they belong to DtPipe.Adapters.Shared (SQL writers, dialects,
/// retry policy, cursor persistence) or, for Arrow bridges, outside Core entirely.
/// </summary>
public class CoreBoundaryTests
{
    [Theory]
    [InlineData("DtPipe.Core.Abstractions.BaseSqlDataWriter")]
    [InlineData("DtPipe.Core.Dialects.BaseSqlDialect")]
    [InlineData("DtPipe.Core.Dialects.PostgreSqlDialect")]
    [InlineData("DtPipe.Core.Dialects.OracleDialect")]
    [InlineData("DtPipe.Core.Dialects.SqlServerDialect")]
    [InlineData("DtPipe.Core.Dialects.SqliteDialect")]
    [InlineData("DtPipe.Core.Dialects.DuckDbDialect")]
    [InlineData("DtPipe.Core.Infrastructure.Retry.DatabaseRetryPolicy")]
    [InlineData("DtPipe.Core.Cursor.CursorStateStore")]
    public void Core_Assembly_Does_Not_Contain_Concrete_Infrastructure(string typeName)
    {
        var type = typeof(DtPipe.Core.Options.OptionsRegistry).Assembly.GetType(typeName);
        Assert.True(type is null, $"{typeName} must not be in the DtPipe.Core assembly.");
    }

    [Fact]
    public void Core_Assembly_Keeps_Engine_Contracts()
    {
        var core = typeof(DtPipe.Core.Options.OptionsRegistry).Assembly;

        Assert.NotNull(core.GetType("DtPipe.Core.Abstractions.Dag.IDagOrchestrator"));
        Assert.NotNull(core.GetType("DtPipe.Core.Pipelines.Dag.IMemoryChannelRegistry"));
        Assert.NotNull(core.GetType("DtPipe.Core.Abstractions.IStreamReader"));
        Assert.NotNull(core.GetType("DtPipe.Core.Abstractions.ISqlDialect")); // abstraction stays
        Assert.NotNull(typeof(DtPipe.Core.Models.Branch));
    }
}
