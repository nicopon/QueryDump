namespace DtPipe.Core.Abstractions;

/// <summary>
/// Indicates that a reader or writer supports customizable batch sizing.
/// </summary>
public interface IBatchSizeConfigurable
{
    /// <summary>
    /// Gets or sets the batch size for record buffering.
    /// </summary>
    int BatchSize { get; set; }
}
