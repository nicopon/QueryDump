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
        string? producedYaml = null;   // resolved plan: yamlContent argument, else regex fallback
        string? argYaml = null;         // from the yamlContent tool-call argument (source of truth, F6)
        string? contentYaml = null;      // regex extraction from free-text content (deprecated fallback)
        bool regexFallbackNoticed = false;  // guards a single, non-logged-noise notice when the fallback is used

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

                             contentYaml = ExtractYamlFromContent(message.Content, contentYaml);
                             producedYaml = ResolveYaml(argYaml, contentYaml, producedYaml, ref regexFallbackNoticed);

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

                     contentYaml = ExtractYamlFromContent(message.Content, contentYaml);
                     producedYaml = ResolveYaml(argYaml, contentYaml, producedYaml, ref regexFallbackNoticed);

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

             // Execute every tool call in this turn (F5). Independent calls run in parallel by
             // default; --sequential forces one-at-a-time execution. Results are appended in the
             // same order as the calls so each "tool" message stays correlated with its call id.
             var calls = lastMsg.ToolCalls!;

                     // F6: the yamlContent tool-call argument is the source of truth. Collect it
                  // across all calls before resolving the plan so it always wins over the regex.
             foreach (var call in calls)
               {
                 if (call.Arguments.ValueKind == JsonValueKind.Object &&
                     call.Arguments.TryGetProperty("yamlContent", out var yamlProp) &&
                     yamlProp.ValueKind == JsonValueKind.String)
                   {
                      argYaml = yamlProp.GetString();
                   }
               }

             var outcomes = new List<ToolInvocationOutcome>(calls.Count);
             if (opts.Sequential)
               {
                 foreach (var call in calls)
                   {
                    outcomes.Add(await InvokeToolInto(call.Name, call.Arguments, ct));
                    toolCounts[call.Name] = toolCounts.GetValueOrDefault(call.Name, 0) + 1;
                   }
               }
             else
               {
                 var tasks = calls
                       .Select(c => InvokeToolInto(c.Name, c.Arguments, ct))
                       .ToArray();
                 await Task.WhenAll(tasks);
                 foreach (var (c, t) in calls.Zip(tasks))
                   {
                    var outcome = await t;
                    outcomes.Add(outcome);
                    toolCounts[c.Name] = toolCounts.GetValueOrDefault(c.Name, 0) + 1;
                   }
               }

             for (int i = 0; i < calls.Count; i++)
               {
                 var call = calls[i];
                 var outcome = outcomes[i];
                 string toolResultRaw = (outcome.Content ?? "{}");
                 string argsFormatted = call.Arguments.ValueKind != JsonValueKind.Undefined ? call.Arguments.ToString() : "{}";

                 toolResultRaw ??= "{}";
                 if (renderTui)
                     _tui.RenderToolResult(call.Name, toolResultRaw, outcome.IsError);
                 if (recordTrajectory)
                     Trajectory.AddStep(Trajectory.Steps.Count + 1, currentReasoning ?? "", call.Name, argsFormatted, toolResultRaw, outcome.IsError);

                 messages.Add(new ChatMessage("tool", toolResultRaw, call.Name, ToolCallId: call.Id));
               }

             producedYaml = ResolveYaml(argYaml, contentYaml, producedYaml, ref regexFallbackNoticed);
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
     /// Extracts a YAML job block from free-text agent content. This is the <b>deprecated
     /// fallback</b> only: the <c>yamlContent</c> tool-call argument is the source of truth (F6).
     /// Returns the most recently extracted block, or <paramref name="current"/> when no block is
     /// found.
     /// </summary>
     [System.Obsolete("Use the yamlContent tool-call argument instead; regex extraction is a logged fallback.")]
     private static string? ExtractYamlFromContent(string? content, string? current)
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

     /// <summary>
     /// Resolves the plan YAML with the single-path precedence required by F6: the
     /// <c>yamlContent</c> tool-call argument wins; the regex-extracted content is only used when
     /// the argument is absent. When the regex fallback is actually used, a single non-silent
     /// notice is logged so the deprecated path is never silent.
     /// </summary>
     private string? ResolveYaml(string? argYaml, string? contentYaml, string? producedYaml, ref bool noticeFallback)
        {
        if (!string.IsNullOrWhiteSpace(argYaml))
            {
             producedYaml = argYaml;
             return producedYaml;
            }

        if (!string.IsNullOrWhiteSpace(contentYaml))
            {
             if (contentYaml != producedYaml)
                 {
                 if (!noticeFallback)
                    {
                     _tui.RenderFallbackNotice();
                     noticeFallback = true;
                     }

                     producedYaml = contentYaml;
                  }

              return producedYaml;
            }

        return producedYaml;
        }
}
