namespace DtPipe.Core.Abstractions;

/// <summary>
/// Indicates that a reader or writer supports customizable batch sizing.
/// </summary>
public interface IBatchSizeConfigurable
{
    /// <summary>
    /// Gets or sets the batch size, in rows, for record buffering.
    /// </summary>
    int BatchSize { get; set; }

    /// <summary>
    /// Gets or sets a soft upper bound, in bytes, on a buffered batch. A batch is flushed as soon
    /// as either <see cref="BatchSize"/> rows or this many bytes have accumulated, whichever comes
    /// first. <c>0</c> (the default) disables the byte bound. The estimate is approximate — the
    /// last row that crosses the bound is kept — so this caps memory without being exact.
    /// </summary>
    long MaxBatchBytes { get; set; }
}
