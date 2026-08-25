using System.Runtime.CompilerServices;
using DtPipe.Cli.Services;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Options;
using DtPipe.Core.Pipelines.Dag;
using DtPipe.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console;
using Xunit;

namespace DtPipe.Tests.Unit.Services;

/// <summary>
/// F16 — cancellation must not mask as success. These tests drive the real
/// LinearPipelineService + ExportService stack with stub readers/writers.
/// </summary>
public class LinearPipelineServiceTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class StubOptions : IOptionSet
    {
        public static string Prefix => "stub";
        public static string DisplayName => "Stub";
    }

    private sealed class BlockingReader : IStreamReader
    {
        public IReadOnlyList<PipeColumnInfo>? Columns => new List<PipeColumnInfo> { new("Id", typeof(int), false) };
        public Task OpenAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        public async IAsyncEnumerable<ReadOnlyMemory<object?[]>> ReadBatchesAsync(int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultedReader : IStreamReader
    {
        public IReadOnlyList<PipeColumnInfo>? Columns => null;
        public Task OpenAsync(CancellationToken ct)
            => throw new InvalidOperationException("root cause X", new ArgumentException("mid layer Y"));
        public async IAsyncEnumerable<ReadOnlyMemory<object?[]>> ReadBatchesAsync(int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubReaderFactory : IStreamReaderFactory
    {
        private readonly Func<IStreamReader> _readerProvider;
        public StubReaderFactory(Func<IStreamReader> readerProvider) => _readerProvider = readerProvider;
        public string ComponentName => "stub";
        public string Category => "Test";
        public Type OptionsType => typeof(StubOptions);
        public bool CanHandle(string connectionString) => false;
        public bool RequiresQuery => false;
        public IEnumerable<Type> GetSupportedOptionTypes() => new[] { typeof(StubOptions) };
        public IStreamReader Create(OptionsRegistry registry) => _readerProvider();
    }

    private sealed class NullWriterFactory : IDataWriterFactory
    {
        public string ComponentName => "null";
        public string Category => "Test";
        public Type OptionsType => typeof(StubOptions);
        public bool CanHandle(string connectionString) => false;
        public IDataWriter Create(OptionsRegistry registry) => throw new NotSupportedException("writer is never opened in these tests");
        public IEnumerable<Type> GetSupportedOptionTypes() => Array.Empty<Type>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Harness
    // ─────────────────────────────────────────────────────────────────────────

    private static (LinearPipelineService Service, Mock<IAnsiConsole> Console) BuildService(IStreamReaderFactory readerFactory)
    {
        var observer = Mock.Of<IExportObserver>();
        var exportService = new ExportService(
            readerFactories: new List<IStreamReaderFactory> { readerFactory },
            writerFactories: new List<IDataWriterFactory> { new NullWriterFactory() },
            transformerFactories: new List<IDataTransformerFactory>(),
            optionsRegistry: new OptionsRegistry(),
            observer: observer,
            logger: NullLogger<ExportService>.Instance,
            hookExecutor: new HookExecutor(observer, NullLogger<HookExecutor>.Instance),
            metricsService: new MetricsService(observer, NullLogger<MetricsService>.Instance),
            schemaValidator: new SchemaValidationService(observer, NullLogger<SchemaValidationService>.Instance),
            pipelineExecutor: new PipelineExecutor(
                Enumerable.Empty<IRowToColumnarBridgeFactory>(),
                Enumerable.Empty<IColumnarToRowBridgeFactory>(),
                NullLogger<PipelineExecutor>.Instance),
            channelRegistry: new MemoryChannelRegistry());

        var services = new ServiceCollection();
        services.AddSingleton(exportService);
        services.AddSingleton<IEnumerable<IStreamReaderFactory>>(new List<IStreamReaderFactory> { readerFactory });
        services.AddSingleton<IEnumerable<IDataWriterFactory>>(new List<IDataWriterFactory> { new NullWriterFactory() });
        services.AddSingleton<IEnumerable<IStreamTransformerFactory>>(new List<IStreamTransformerFactory>());
        var serviceProvider = services.BuildServiceProvider();

        var consoleMock = new Mock<IAnsiConsole>();
        var service = new LinearPipelineService(
            contributors: Array.Empty<DtPipe.Cli.Infrastructure.ICliContributor>(),
            serviceProvider: serviceProvider,
            channelRegistry: new MemoryChannelRegistry(),
            optionsRegistry: new OptionsRegistry(),
            console: consoleMock.Object);
        return (service, consoleMock);
    }

    private static JobDefinition BlockingJob() => new() { Input = "stub:block", Output = null, Limit = 0 };

    // ─────────────────────────────────────────────────────────────────────────
    // Facts
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task User_Ctrl_C_Returns_130()
    {
        var (service, _) = BuildService(new StubReaderFactory(() => new BlockingReader()));
        using var userCts = new CancellationTokenSource();

        var task = service.ExecuteAsync(BlockingJob(), context: null, token: CancellationToken.None, userCancellationToken: userCts.Token);

        // Give the pipeline time to reach the blocking OpenAsync, then simulate Ctrl-C.
        await Task.Delay(200);
        await userCts.CancelAsync();

        var exitCode = await task;
        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task Internal_Cancellation_Propagates_Not_Masks()
    {
        var (service, _) = BuildService(new StubReaderFactory(() => new BlockingReader()));
        using var internalCts = new CancellationTokenSource();

        var task = service.ExecuteAsync(BlockingJob(), context: null, token: internalCts.Token, userCancellationToken: CancellationToken.None);

        await Task.Delay(200);
        await internalCts.CancelAsync();

        // Internal cancellation must reach the caller as an exception, not a 0/130 exit code.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Exception_Chain_Is_Preserved_In_Error_Output()
    {
        var (service, consoleMock) = BuildService(new StubReaderFactory(() => new FaultedReader()));

        var exitCode = await service.ExecuteAsync(new JobDefinition { Input = "stub:fault", Output = "null:-" }, context: null, token: CancellationToken.None, userCancellationToken: CancellationToken.None);

        Assert.Equal(1, exitCode);
        consoleMock.Verify(c => c.Write(It.IsAny<Markup>()), Times.AtLeastOnce);
    }

    [Fact]
    public void ExceptionChainFlattener_Formats_Full_Chain()
    {
        var ex = new InvalidOperationException("root cause X", new ArgumentException("mid layer Y"));

        var formatted = DtPipe.Core.Infrastructure.Diagnostics.ExceptionChainFlattener.Format(ex);

        Assert.Contains("InvalidOperationException: root cause X", formatted, StringComparison.Ordinal);
        Assert.Contains("ArgumentException: mid layer Y", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionChainFlattener_Unwraps_SingleFault_AggregateException()
    {
        var ex = new AggregateException("wrapper", new InvalidOperationException("real cause"));

        var formatted = DtPipe.Core.Infrastructure.Diagnostics.ExceptionChainFlattener.Format(ex);

        Assert.DoesNotContain("wrapper", formatted, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: real cause", formatted, StringComparison.Ordinal);
    }
}
