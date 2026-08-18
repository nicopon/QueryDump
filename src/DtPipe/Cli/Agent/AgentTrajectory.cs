using System;
using System.Collections.Generic;

namespace DtPipe.Cli.Agent;

public class TrajectoryStep
{
    public int Iteration { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Reasoning { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolArgs { get; set; }
    public string? ToolResult { get; set; }
    public bool IsError { get; set; }
}

public class AgentTrajectory
 {
    public List<TrajectoryStep> Steps { get; } = new();
    public string? LastGeneratedYaml { get; set; }

     /// <summary>
     /// Determinism report produced when the validated plan is replicated N times (<c>--repeat</c>).
     /// Null when replication is not requested.
     /// </summary>
    public DeterminismReport? Determinism { get; set; }

    public void AddStep(int iteration, string reasoning, string? toolName = null, string? toolArgs = null, string? toolResult = null, bool isError = false)
     {
        Steps.Add(new TrajectoryStep
          {
            Iteration = iteration,
            Timestamp = DateTime.Now,
            Reasoning = reasoning,
            ToolName = toolName,
            ToolArgs = toolArgs,
            ToolResult = toolResult,
            IsError = isError
          });
     }
 }

 /// <summary>
 /// Result of replicating a validated plan N times to measure determinism.
 /// </summary>
public class DeterminismReport
 {
    /// <summary>Number of replications executed.</summary>
    public int Repetitions { get; init; }

     /// <summary>Distinct generated YAML payloads observed across replications.</summary>
    public List<string> DistinctYaml { get; init; } = new();

     /// <summary>Number of distinct YAML payloads (1 => fully deterministic).</summary>
    public int Variance => DistinctYaml.Count;

     /// <summary>True when all replications produced byte-for-byte identical YAML.</summary>
    public bool IsDeterministic => Variance == 1;

     /// <summary>Whether a single YAML payload was observed.</summary>
    public bool HasYaml => DistinctYaml.Count > 0;
 }
