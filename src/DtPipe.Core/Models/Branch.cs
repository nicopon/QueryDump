using DtPipe.Core.Options;

namespace DtPipe.Core.Models;

/// <summary>
/// F7 — canonical per-branch engine-control settings (limit/batch/sampling/dry-run/
/// metrics/log/prefix/cursor/state). One authoritative bundle: the CLI converter derives
/// it once (global defaults overlaid by branch-local flags) and every consumer reads it
/// instead of re-parsing flag dictionaries.
/// </summary>
public sealed record BranchEngineSettings(
    int Limit,
    int BatchSize,
    double SamplingRate,
    int? SamplingSeed,
    int DryRunCount,
    bool NoStats,
    string? MetricsPath,
    string? LogPath,
    string? Prefix,
    string? Cursor,
    string? State)
{
    public static BranchEngineSettings Default { get; }
        = new(Limit: 0, BatchSize: PipelineOptions.DefaultBatchSize, SamplingRate: 1.0, SamplingSeed: null,
              DryRunCount: 0, NoStats: false, MetricsPath: null, LogPath: null, Prefix: null,
              Cursor: null, State: null);

    /// <summary>Applies these settings onto a job definition (single derivation point).</summary>
    public JobDefinition ApplyTo(JobDefinition job) => job with
    {
        Limit = Limit,
        BatchSize = BatchSize,
        SamplingRate = SamplingRate,
        SamplingSeed = SamplingSeed,
        DryRunCount = DryRunCount,
        NoStats = NoStats || job.NoStats,
        MetricsPath = MetricsPath ?? job.MetricsPath,
        LogPath = LogPath ?? job.LogPath,
        Prefix = Prefix ?? job.Prefix,
        Cursor = Cursor ?? job.Cursor,
        State = State ?? job.State,
    };
}

/// <summary>
/// F7 — canonical description of one pipeline branch: DAG routing plus its engine
/// settings. This is the authoritative model; JobDefinition carries provider-level data
/// and CliJobContext only transient binding info.
/// </summary>
public sealed record Branch(
    string Alias,
    string? Input,
    string? Output,
    IReadOnlyList<string> StreamingAliases,
    IReadOnlyList<string> RefAliases,
    string? ProcessorName)
{
    public bool HasStreamTransformer => ProcessorName != null;
}
