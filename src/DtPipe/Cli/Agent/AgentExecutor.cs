using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DtPipe.Cli.Mcp;
using Spectre.Console;

namespace DtPipe.Cli.Agent;

public class AgentExecutor
{
    private readonly IAgentToolProvider _toolProvider;
    private readonly ILlmClient _llmClient;
    private readonly AgentTui _tui;
    private readonly IAnsiConsole _console;
    private readonly ConversationWindowManager _windowManager = new();

    public AgentTrajectory Trajectory { get; } = new();
    public List<ChatMessage> Messages { get; } = new();

    public AgentExecutor(IAgentToolProvider toolProvider, ILlmClient llmClient, AgentTui tui, IAnsiConsole console)
     {
         _toolProvider = toolProvider;
         _llmClient = llmClient;
         _tui = tui;
         _console = console;

        Messages.Add(new ChatMessage("system", AgentSystemPrompt.DefaultSystemPrompt));
     }

    /// <summary>
    /// Runs a single agent turn. When <see cref="AgentOptions.Repeat"/> &gt; 1 the validated plan
    /// is replicated that many times (each from a fresh conversation) and a
    /// <see cref="DeterminismReport"/> is attached to the trajectory (F3 — determinism).
    /// </summary>
    public async Task<int> RunTurnAsync(
        string userPrompt,
        string model,
        string baseUrl,
        AgentOptions? options = null,
        int maxIterations = 25,
        CancellationToken ct = default)
     {
        var opts = options ?? new AgentOptions();
        Messages.Add(new ChatMessage("user", userPrompt));

        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();

        // Primary run uses the instance Messages so the interactive / inspection flows keep state.
        var primary = await RunPlanningLoopAsync(Messages, userPrompt, model, baseUrl, opts, maxIterations,
            recordTrajectory: true, renderTui: true, ct);
        bool success = primary.Success;
        int turnIterations = primary.Iterations;
        foreach (var kv in primary.ToolCounts)
            toolCounts[kv.Key] = kv.Value;

        var observedYamls = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary.Yaml))
            observedYamls.Add(primary.Yaml);

        // Replication (F3): re-run the planning loop from a fresh conversation with the same
        // temperature/seed and compare the generated YAML to measure variance.
        int repls = Math.Max(1, opts.Repeat);
        for (int r = 1; r < repls; r++)
         {
            var fresh = new List<ChatMessage>
             {
                new("system", AgentSystemPrompt.DefaultSystemPrompt),
                new("user", userPrompt)
             };
            var repl = await RunPlanningLoopAsync(fresh, userPrompt, model, baseUrl, opts, maxIterations,
                recordTrajectory: false, renderTui: false, ct);
            if (!string.IsNullOrWhiteSpace(repl.Yaml))
                observedYamls.Add(repl.Yaml);
         }

        Trajectory.Determinism = BuildDeterminismReport(repls, observedYamls);

        stopwatch.Stop();
         _tui.RenderFinalSummary(success, turnIterations <= maxIterations ? turnIterations : maxIterations, toolCounts, stopwatch.Elapsed);

        return success ? 0 : 1;
     }

    private async Task<(bool Success, string? Yaml, int Iterations, Dictionary<string, int> ToolCounts)> RunPlanningLoopAsync(
        List<ChatMessage> messages,
        string userPrompt,
        string model,
        string baseUrl,
        AgentOptions opts,
        int maxIterations,
        bool recordTrajectory,
        bool renderTui,
        CancellationToken ct)
     {
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? producedYaml = null;

        bool success = false;
        int turnIterations = 1;

        while (turnIterations <= maxIterations)
         {
            int currentStepNum = Trajectory.Steps.Count + 1;
            string? currentReasoning = null;
            string? currentToolName = null;

            LlmResponse? response = null;

            var compactedMessages = _windowManager.Compact(messages);

            if (renderTui)
              {
                response = await _console.Status()
                      .Spinner(Spinner.Known.Dots)
                      .SpinnerStyle(Style.Parse("blue bold"))
                      .StartAsync<LlmResponse>($"Agent thinking (Step {currentStepNum})...", async ctx =>
                      {
                        response = await _llmClient.ChatAsync(baseUrl, model, compactedMessages,
                             _toolProvider.GetToolDefinitions(), 16384,
                            temperature: opts.Temperature, seed: opts.Seed, ct);

                        if (string.IsNullOrEmpty(response.Error))
                          {
                            var message = response.Message;
                            messages.Add(message);
                            currentReasoning = message.Content;

                            producedYaml = ApplyYamlExtraction(currentReasoning, producedYaml);

                            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                              {
                                currentToolName = message.ToolCalls[0].Name;
                              }
                          }

                        return response;
                      });
              }
            else
              {
                response = await _llmClient.ChatAsync(baseUrl, model, compactedMessages,
                     _toolProvider.GetToolDefinitions(), 16384,
                    temperature: opts.Temperature, seed: opts.Seed, ct);

                if (string.IsNullOrEmpty(response.Error))
                  {
                    var message = response.Message;
                    messages.Add(message);
                    currentReasoning = message.Content;

                    producedYaml = ApplyYamlExtraction(currentReasoning, producedYaml);

                    if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                      {
                        currentToolName = message.ToolCalls[0].Name;
                      }
                  }
              }

            if (response == null || !string.IsNullOrEmpty(response.Error))
             {
                string errMsg = response?.Error ?? "No response received from LLM.";
                if (renderTui)
                    _tui.RenderAgentResponse($"Error calling LLM: {errMsg}");
                if (recordTrajectory)
                    Trajectory.AddStep(currentStepNum, $"LLM Error: {errMsg}");
                success = false;
                break;
             }

             // Compact iteration log
            if (renderTui)
                _tui.RenderCompactIterationStatus(currentStepNum, maxIterations, currentReasoning, currentToolName);

            var lastMsg = messages[^1];
            if (lastMsg.ToolCalls == null || lastMsg.ToolCalls.Count == 0)
             {
                 // Agent finished this turn with text response
                if (renderTui)
                    _tui.RenderAgentResponse(lastMsg.Content ?? "");
                if (recordTrajectory)
                    Trajectory.AddStep(currentStepNum, currentReasoning ?? "Finished response.");
                success = true;
                break;
             }

             // Execute tool call
            var toolCall = lastMsg.ToolCalls[0];
            var toolName = toolCall.Name;
            var args = toolCall.Arguments;

            toolCounts[toolName] = toolCounts.GetValueOrDefault(toolName, 0) + 1;
            string argsFormatted = args.ValueKind != JsonValueKind.Undefined ? args.ToString() : "{}";

             // Prefer the yamlContent tool-call argument as the source of truth; keep the regex
             // fallback (F6 will mark it a logged, deprecated fallback).
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("yamlContent", out var yamlProp) && yamlProp.ValueKind == JsonValueKind.String)
             {
                producedYaml = yamlProp.GetString();
             }

            string toolResultRaw = "{}";
            bool isError = false;

            if (renderTui)
              {
                await _console.Status()
                      .Spinner(Spinner.Known.Default)
                      .SpinnerStyle(Style.Parse("magenta bold"))
                      .StartAsync($"Executing tool '{toolName}'...", async ctx =>
                      {
                        var outcome = await InvokeToolInto(toolName, args, ct);
                        toolResultRaw = outcome.Content;
                        isError = outcome.IsError;
                      });
              }
            else
              {
                var outcome = await InvokeToolInto(toolName, args, ct);
                toolResultRaw = outcome.Content;
                isError = outcome.IsError;
              }

            toolResultRaw ??= "{}";
            if (recordTrajectory)
                Trajectory.AddStep(currentStepNum, currentReasoning ?? "", toolName, argsFormatted, toolResultRaw, isError);

            messages.Add(new ChatMessage("tool", toolResultRaw, toolName, ToolCallId: toolCall.Id));
            turnIterations++;
         }

        // Promote the primary run's YAML to the instance trajectory for interactive / inspection flows.
        if (recordTrajectory && !string.IsNullOrWhiteSpace(producedYaml))
            Trajectory.LastGeneratedYaml = producedYaml;

        return (success, producedYaml, turnIterations, toolCounts);
     }

    private async Task<ToolInvocationOutcome> InvokeToolInto(string toolName, JsonElement args, CancellationToken ct)
      {
        try
          {
            var parsedResult = await _toolProvider.InvokeToolAsync(toolName, args, ct);
            return new ToolInvocationOutcome(parsedResult.Content, parsedResult.IsError);
          }
        catch (Exception ex)
          {
            return new ToolInvocationOutcome(JsonSerializer.Serialize(new { error = ex.Message }), true);
          }
      }

    /// <summary>
    /// Builds the <see cref="DeterminismReport"/> from the YAMLs observed across replications.
    /// When only one replication was requested, a report is still produced (variance is 0 by
    /// definition) so the trajectory always carries a determinism signal.
    /// </summary>
    private static DeterminismReport BuildDeterminismReport(int repetitions, List<string> observedYamls)
     {
        var distinct = new List<string>();
        foreach (var y in observedYamls)
         {
            if (!distinct.Contains(y))
                distinct.Add(y);
         }

        return new DeterminismReport
         {
            Repetitions = repetitions,
            DistinctYaml = distinct
         };
     }

    /// <summary>
    /// Extracts a YAML job block from free-text agent content (fallback only — the
    /// <c>yamlContent</c> tool argument is the source of truth, F6).
    /// </summary>
     private static string? ApplyYamlExtraction(string? content, string? current)
       {
        if (string.IsNullOrWhiteSpace(content))
            return current;

        var match = Regex.Match(content, @"```yaml\s*(?<yaml>[\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (match.Success)
         {
            var extracted = match.Groups["yaml"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted) && (extracted.Contains("input:") || extracted.Contains("output:")))
             {
                return extracted;
             }
         }

        return current;
     }
}
