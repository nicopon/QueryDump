namespace DtPipe.Core.Options;

/// <summary>
/// F13 — implemented by writer/reader options that carry a target table concept.
/// Replaces reflection <c>GetProperty("Table")</c> lookups with a typed capability.
/// </summary>
public interface ITableAwareOptions
{
    string? Table { get; }
}

