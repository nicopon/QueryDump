namespace DtPipe.Core.Abstractions;

/// <summary>
/// F13 — marker for stream-processor factories (the --sql / --merge family).
/// Lets the host classify processors without comparing ComponentName strings.
/// </summary>
public interface IStreamProcessorMarker
{
}
