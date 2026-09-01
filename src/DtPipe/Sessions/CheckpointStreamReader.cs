using System.Runtime.CompilerServices;
using Apache.Arrow;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Infrastructure.Arrow;
using DtPipe.Core.Models;

namespace DtPipe.Sessions;

/// <summary>
/// Reads a materialised checkpoint back as a pipeline source, so a run can resume from a point
/// instead of going back to Oracle.
///
/// It is a columnar reader, which is what keeps the resumption zero-copy: the bytes were written
/// as Arrow and come back as Arrow, without a row round-trip in between.
/// </summary>
public sealed class CheckpointStreamReader : IColumnarStreamReader
{
    private readonly CheckpointStore _store;
    private readonly string _checkpointKey;

    public CheckpointStreamReader(CheckpointStore store, string checkpointKey)
    {
        _store = store;
        _checkpointKey = checkpointKey;
    }

    public Schema? Schema { get; private set; }
    public IReadOnlyList<PipeColumnInfo>? Columns { get; private set; }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        Schema = await _store.ReadSchemaAsync(_checkpointKey, ct)
                 ?? throw new InvalidOperationException(
                     $"Checkpoint '{_checkpointKey}' holds no batches, so it has no schema to resume from.");
        Columns = ArrowSchemaFactory.ToPipeColumns(Schema);
    }

    public IAsyncEnumerable<RecordBatch> ReadRecordBatchesAsync(CancellationToken ct = default)
        => _store.ReadAsync(_checkpointKey, ct);

    public async IAsyncEnumerable<ReadOnlyMemory<object?[]>> ReadBatchesAsync(
        int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in ReadRecordBatchesAsync(ct))
        {
            using (batch)
            {
                foreach (var memory in ArrowRowConverter.FlattenBatch(batch, batchSize))
                    yield return memory;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
