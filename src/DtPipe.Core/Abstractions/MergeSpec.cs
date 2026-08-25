using DtPipe.Core.Models;

namespace DtPipe.Core.Abstractions;

/// <summary>Conflict-handling mode for a staged merge (F9).</summary>
public enum MergeMode
{
    /// <summary>Insert; on key conflict update non-key columns.</summary>
    Upsert,
    /// <summary>Insert; skip rows whose key already exists.</summary>
    Ignore,
    /// <summary>Plain insert, no conflict handling.</summary>
    Insert,
}

/// <summary>
/// F9 — shared description of a staged merge. Table names are pre-quoted by the caller
/// (they may be schema-qualified composites); column and key names are raw and quoted
/// by the dialect implementation itself.
/// </summary>
/// <param name="QuotedTargetTable">Pre-quoted target table reference.</param>
/// <param name="SourceTable">Pre-quoted staging/source table reference.</param>
/// <param name="KeyColumns">Raw key column names.</param>
/// <param name="Columns">Full target column set.</param>
/// <param name="Mode">Conflict-handling mode.</param>
/// <param name="ConstraintVerified">
/// When false (DuckDB/SQLite), the dialect may fall back to a DELETE+INSERT script —
/// the writer's introspection supplies this flag.
/// </param>
public sealed record MergeSpec(
    string QuotedTargetTable,
    string SourceTable,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<PipeColumnInfo> Columns,
    MergeMode Mode,
    bool ConstraintVerified = true);
