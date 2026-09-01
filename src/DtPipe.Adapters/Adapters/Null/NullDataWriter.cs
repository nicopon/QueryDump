using Apache.Arrow;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;

namespace DtPipe.Adapters.Null;

/// <summary>
/// Discards everything, in whichever mode the pipeline is already in.
///
/// It implements BOTH writer contracts on purpose. The engine picks row or columnar mode from
/// the writer's interface, so a row-only sink forces a columnar pipeline through a row bridge —
/// and this writer exists to measure reader and transformer throughput, which means measuring
/// the bridge instead of the pipeline. Accepting either shape is the only way it can be
/// transparent to what it is asked to observe.
/// </summary>
public class NullDataWriter : IRowDataWriter, IColumnarDataWriter
{
    public string ComponentName => "null";
    public ValueTask InitializeAsync(IReadOnlyList<PipeColumnInfo> columns, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask WriteBatchAsync(IReadOnlyList<object?[]> batch, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>Takes ownership like any columnar writer: Arrow buffers are off-heap, so a
    /// dropped reference is a leak the GC never reports.</summary>
    public ValueTask WriteRecordBatchAsync(RecordBatch batch, CancellationToken ct = default)
    {
        batch.Dispose();
        return ValueTask.CompletedTask;
    }
    public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask ExecuteCommandAsync(string command, CancellationToken ct = default)
        => throw new NotSupportedException("Executing raw commands is not supported for the null writer.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
