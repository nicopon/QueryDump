using System.Collections.Generic;

namespace DtPipe.Cli.Agent;

/// <summary>
/// Operating mode of the agent. Controls the available tool set and whether the LLM
/// is allowed to drive real execution.
/// </summary>
public enum AgentMode
 {
    /// <summary>
    /// The LLM plans and validates a pipeline (produces a validated <see cref="AgentPlan"/>).
    /// Execution is a deterministic step run by the engine, never by the LLM.
    /// <c>execute-yaml-job</c> is NOT in the tool allow-list.
    /// </summary>
    Plan,

    /// <summary>
    /// The LLM may drive execution end-to-end. Execution is gated by the guardrails
    /// (dry-run by default, approval gate, SQL safety policy).
    /// </summary>
    Execute,

    /// <summary>
    /// Combines planning and execution. The LLM plans, validates, then executes through
    /// the guardrails (still dry-run by default unless <c>apply</c> + approval are granted).
    /// </summary>
    Autonomous
}

/// <summary>
/// Configuration for a single agent run. All fields are optional with safe defaults so
/// that <c>dtpipe agent</c> with no flags is the safest behavior:
/// mode = Plan, dry-run, deterministic (temperature 0, seed 0).
/// </summary>
public sealed class AgentOptions
 {
    public AgentMode Mode { get; init; } = AgentMode.Plan;

      /// <summary>Sampling temperature. 0 => fully deterministic decoding.</summary>
    public double Temperature { get; init; } = 0.0;

      /// <summary>Optional fixed seed for reproducible sampling. Null => provider picks its own.</summary>
    public int? Seed { get; init; } = 0;

      /// <summary>Number of replications of the validated plan for determinism/variance measurement.</summary>
    public int Repeat { get; init; } = 1;

      /// <summary>When true, independent tool calls are executed sequentially instead of in parallel.</summary>
    public bool Sequential { get; init; } = false;

      /// <summary>Optional rolling-summary compaction (a second LLM call). Off by default (KISS).</summary>
    public bool RollingSummary { get; init; } = false;

      /// <summary>Allow destructive SQL verbs (DROP/DELETE/TRUNCATE/...). Default deny.</summary>
    public bool AllowDestructive { get; init; } = false;

      /// <summary>Allow network access in SQL (LOAD httpfs/azure, remote read_parquet). Default deny.</summary>
    public bool AllowNetwork { get; init; } = false;

      /// <summary>Whether execute-yaml-job performs a real write. Default false (dry-run).</summary>
    public bool Apply { get; init; } = false;

      /// <summary>Restore the legacy monolithic ReAct behavior (single tool call per iteration).</summary>
    public bool LegacyAgent { get; init; } = false;
}

/// <summary>
/// A validated pipeline plan produced by the planner. Execution of this plan is a
/// deterministic engine step — the LLM no longer drives execution.
/// </summary>
public sealed class AgentPlan
 {
     /// <summary>The source-of-truth YAML configuration (from the <c>yamlContent</c> tool argument).</summary>
    public string Yaml { get; init; } = string.Empty;

      /// <summary>Parsed DAG definition backing the plan.</summary>
    public DtPipe.Core.Pipelines.Dag.JobDagDefinition? DagDefinition { get; init; }

      /// <summary>Validation report (empty list => valid).</summary>
    public List<string> ValidationReport { get; init; } = new();

      /// <summary>Operating mode the plan was produced under.</summary>
    public AgentMode Mode { get; init; } = AgentMode.Plan;

      /// <summary>True when <see cref="ValidationReport"/> is empty.</summary>
    public bool IsValid => ValidationReport.Count == 0;
}