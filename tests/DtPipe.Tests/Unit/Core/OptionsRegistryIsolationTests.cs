using DtPipe.Adapters.Csv;
using DtPipe.Core.Options;
using Xunit;

namespace DtPipe.Tests.Unit.Core;

/// <summary>
/// Tests for OptionsRegistry.BeginScope() — ensures concurrent DAG branches
/// each get an isolated copy of the options dictionary.
///
/// Background: OptionsRegistry uses AsyncLocal&lt;Dictionary&lt;Type, object&gt;&gt;.
/// AsyncLocal isolates reference assignments, but NOT in-place mutations of a
/// shared object. Without BeginScope(), branches inherit the same Dictionary
/// reference and overwrite each other's options.
/// BeginScope() performs a copy-on-write fork: it creates a new dict (copying
/// parent entries), then reassigns _options.Value — isolating all subsequent
/// writes to the current async context.
/// </summary>
public class OptionsRegistryIsolationTests
{
    [Fact]
    public async Task BeginScope_IsolatesConcurrentBranches()
    {
        var registry = new OptionsRegistry();
        var branchBDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string? aRead = null;
        string? bRead = null;

        async Task BranchA()
        {
            await Task.Yield(); // mirrors ExecuteBranchAsync's await Task.Yield()
            registry.BeginScope();
            registry.Register(new CsvReaderOptions { ColumnTypes = "branch-A" });
            await branchBDone.Task; // wait until B has also written, to force the race
            aRead = registry.Get<CsvReaderOptions>().ColumnTypes;
        }

        async Task BranchB()
        {
            await Task.Yield();
            registry.BeginScope();
            registry.Register(new CsvReaderOptions { ColumnTypes = "branch-B" });
            branchBDone.SetResult();
            bRead = registry.Get<CsvReaderOptions>().ColumnTypes;
        }

        await Task.WhenAll(BranchA(), BranchB());

        Assert.Equal("branch-A", aRead); // branch A must not see branch B's value
        Assert.Equal("branch-B", bRead);
    }

    [Fact]
    public async Task BeginScope_InheritsParentOptions()
    {
        var registry = new OptionsRegistry();
        registry.Register(new CsvReaderOptions { Separator = "|" });

        string? separatorInBranch = null;

        async Task Branch()
        {
            await Task.Yield();
            registry.BeginScope();
            // branch does not override Separator — it should inherit the parent value
            separatorInBranch = registry.Get<CsvReaderOptions>().Separator;
        }

        await Branch();

        Assert.Equal("|", separatorInBranch);
    }

    [Fact]
    public async Task BeginScope_BranchWriteDoesNotLeakToSibling()
    {
        var registry = new OptionsRegistry();
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string? bRead = null;

        async Task BranchA()
        {
            await Task.Yield();
            registry.BeginScope();
            bStarted.SetResult();
            await Task.Yield(); // let B run its BeginScope
            registry.Register(new CsvReaderOptions { ColumnTypes = "only-A" });
            aWritten.SetResult();
        }

        async Task BranchB()
        {
            await bStarted.Task;
            await Task.Yield();
            registry.BeginScope();
            await aWritten.Task; // wait until A has written
            bRead = registry.Get<CsvReaderOptions>().ColumnTypes; // should see "" (default), not "only-A"
        }

        await Task.WhenAll(BranchA(), BranchB());

        Assert.Equal("", bRead); // B's scope was forked before A wrote — A's write is invisible to B
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F17 — silent failures made loud
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Generic.List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class ProbeOptions : IOptionSet
    {
        public static string Prefix => "probe";
        public static string DisplayName => "Probe";
    }

    [Fact]
    public void Get_Missing_Logs_Warning()
    {
        var logger = new CapturingLogger();
        var registry = new OptionsRegistry(logger);

        var options = registry.Get<ProbeOptions>();

        Assert.NotNull(options);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("ProbeOptions", warning);
    }

    [Fact]
    public void Get_Hit_Does_Not_Log_Warning()
    {
        var logger = new CapturingLogger();
        var registry = new OptionsRegistry(logger);
        registry.Register(new ProbeOptions());

        registry.Get<ProbeOptions>();

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void TryGet_Missing_Returns_False()
    {
        var registry = new OptionsRegistry();

        var found = registry.TryGet<ProbeOptions>(out var value);

        Assert.False(found);
        Assert.NotNull(value);
        Assert.False(registry.TryGet<CsvReaderOptions>(out _));
    }

    [Fact]
    public void TryGet_Hit_Returns_Registered_Instance()
    {
        var registry = new OptionsRegistry();
        var registered = new CsvReaderOptions { Separator = ";" };
        registry.Register(registered);

        var found = registry.TryGet<CsvReaderOptions>(out var value);

        Assert.True(found);
        Assert.Same(registered, value);
    }

    [Fact]
    public void Require_Missing_Throws()
    {
        var registry = new OptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Require<ProbeOptions>());

        Assert.Contains("ProbeOptions", ex.Message);
    }

    [Fact]
    public void Require_Hit_Returns_Registered_Instance()
    {
        var registry = new OptionsRegistry();
        var registered = new CsvReaderOptions { Separator = ";" };
        registry.Register(registered);

        Assert.Same(registered, registry.Require<CsvReaderOptions>());
    }
}
