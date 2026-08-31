using Apache.Arrow;

namespace DtPipe.Core.Infrastructure.Arrow;

/// <summary>
/// Shared-ownership helpers for the columnar pipeline. See <c>CLAUDE.md</c> ›
/// "RecordBatch ownership (columnar path)" for the contract these enforce.
///
/// A <see cref="RecordBatch"/> has one owner at a time; the owner calls <c>Dispose()</c>
/// exactly once. When a stage emits a new batch that reuses another batch's column buffers, it must
/// retain those columns so the two batches can be disposed independently — <see cref="ArrayData.Retain"/>
/// bumps a reference count on the underlying buffers rather than copying them.
/// </summary>
public static class ArrowOwnership
{
    /// <summary>
    /// Returns a view over <paramref name="array"/> that keeps its buffers alive via reference
    /// counting. Dispose the returned array (or the batch that holds it) when done.
    /// </summary>
    public static IArrowArray RetainArray(IArrowArray array)
        => global::Apache.Arrow.ArrowArrayFactory.BuildArray(array.Data.Retain());

    /// <summary>
    /// Returns a new <see cref="RecordBatch"/> whose columns are retained views over
    /// <paramref name="batch"/>'s columns. The source and the returned batch can be disposed
    /// independently. Used for fan-out, where one upstream batch feeds several consumers.
    /// </summary>
    public static RecordBatch RetainAll(RecordBatch batch)
    {
        int columnCount = batch.Schema.FieldsList.Count;
        var arrays = new IArrowArray[columnCount];
        for (int i = 0; i < columnCount; i++)
            arrays[i] = RetainArray(batch.Column(i));
        return new RecordBatch(batch.Schema, arrays, batch.Length);
    }
}
