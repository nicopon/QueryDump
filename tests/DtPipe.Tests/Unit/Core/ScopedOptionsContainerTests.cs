using DtPipe.Adapters.Csv;
using DtPipe.Core.Options;
using Xunit;

namespace DtPipe.Tests.Unit.Core;

/// <summary>
/// F14 — ScopedOptionsContainer: explicit scope isolation (one container per scope, no
/// AsyncLocal), thread-safe registration and TryGet/Require semantics.
/// </summary>
public class ScopedOptionsContainerTests
{
    [Fact]
    public void Scopes_Are_Isolated_By_Construction()
    {
        var scopeA = new ScopedOptionsContainer();
        var scopeB = new ScopedOptionsContainer();

        scopeA.Register(new CsvReaderOptions { ColumnTypes = "branch-A" });

        Assert.True(scopeA.TryGet<CsvReaderOptions>(out _));
        Assert.False(scopeB.TryGet<CsvReaderOptions>(out _)); // no leakage
    }

    [Fact]
    public void Concurrent_Registration_Is_Thread_Safe()
    {
        var container = new ScopedOptionsContainer();
        Parallel.For(0, 64, i => container.RegisterByType(typeof(CsvReaderOptions), new CsvReaderOptions()));

        Assert.True(container.Has<CsvReaderOptions>());
    }

    [Fact]
    public void Require_Missing_Throws()
    {
        var container = new ScopedOptionsContainer();
        Assert.Throws<InvalidOperationException>(() => container.Require<CsvReaderOptions>());
    }

    [Fact]
    public void TryGet_Miss_Returns_False_With_Default_Instance()
    {
        var container = new ScopedOptionsContainer();
        var found = container.TryGet<CsvReaderOptions>(out var value);
        Assert.False(found);
        Assert.NotNull(value);
    }
}
