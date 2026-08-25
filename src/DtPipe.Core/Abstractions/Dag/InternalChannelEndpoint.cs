namespace DtPipe.Core.Abstractions.Dag;

/// <summary>Transport kind of an internal memory channel.</summary>
public enum InternalChannelKind
{
    /// <summary>Row batches (<c>object?[]</c>) over a native channel.</summary>
    Row,
    /// <summary>Arrow <c>RecordBatch</c>es over a native channel.</summary>
    Arrow
}

/// <summary>
/// A typed channel endpoint handed to a branch by the orchestrator (F5): the engine
/// passes structured routing instead of emitting CLI flag syntax for the host to re-parse.
/// </summary>
public sealed record InternalChannelEndpoint(string Alias, InternalChannelKind Kind);

/// <summary>
/// Capability marker for reader/writer factories that transport data over internal
/// memory channels. Replaces adapter-identity string checks ("arrow-memory"/"memory-channel").
/// </summary>
public interface IInternalChannelCapable
{
    InternalChannelKind ChannelKind { get; }
}

/// <summary>
/// Marker for reader factories whose source is a stream processor (SQL / merge) rather
/// than an external connection. Replaces the "stream-transformer" identity-string check.
/// </summary>
public interface IStreamProcessorSource
{
}
