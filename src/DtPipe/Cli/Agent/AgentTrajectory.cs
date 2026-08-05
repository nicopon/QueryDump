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
