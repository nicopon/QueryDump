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

    public async Task<int> RunTurnAsync(
        string userPrompt,
        string model,
        string baseUrl,
        int maxIterations = 25,
        CancellationToken ct = default)
    {
        Messages.Add(new ChatMessage("user", userPrompt));

        var tools = _toolProvider.GetToolDefinitions();
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();
        bool success = false;
        int turnIterations = 1;

        while (turnIterations <= maxIterations)
        {
            int currentStepNum = Trajectory.Steps.Count + 1;
            string? currentReasoning = null;
            string? currentToolName = null;

            LlmResponse? response = null;

            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue bold"))
                .StartAsync($"Agent thinking (Step {currentStepNum})...", async ctx =>
                {
                    var compactedMessages = _windowManager.Compact(Messages);
                    response = await _llmClient.ChatAsync(baseUrl, model, compactedMessages, tools, 16384, ct);
                    
                    if (string.IsNullOrEmpty(response.Error))
                    {
                        var message = response.Message;
                        Messages.Add(message);
                        currentReasoning = message.Content;

                        ExtractYamlFromContent(currentReasoning);

                        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                        {
                            var toolCall = message.ToolCalls[0];
                            currentToolName = toolCall.Name;
                        }
                    }
                });

            if (response == null || !string.IsNullOrEmpty(response.Error))
            {
                string errMsg = response?.Error ?? "No response received from LLM.";
                _tui.RenderAgentResponse($"Error calling LLM: {errMsg}");
                Trajectory.AddStep(currentStepNum, $"LLM Error: {errMsg}");
                success = false;
                break;
            }

            // Compact iteration log
            _tui.RenderCompactIterationStatus(currentStepNum, maxIterations, currentReasoning, currentToolName);

            var lastMsg = Messages[^1];
            if (lastMsg.ToolCalls == null || lastMsg.ToolCalls.Count == 0)
            {
                // Agent finished this turn with text response
                _tui.RenderAgentResponse(lastMsg.Content ?? "");
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

            // Extract YAML if arguments contain yamlContent
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("yamlContent", out var yamlProp) && yamlProp.ValueKind == JsonValueKind.String)
            {
                Trajectory.LastGeneratedYaml = yamlProp.GetString();
            }

            string toolResultRaw = "{}";
            bool isError = false;

            await _console.Status()
                .Spinner(Spinner.Known.Default)
                .SpinnerStyle(Style.Parse("magenta bold"))
                .StartAsync($"Executing tool '{toolName}'...", async ctx =>
                {
                    try
                    {
                        var parsedResult = await _toolProvider.InvokeToolAsync(toolName, args, ct);
                        toolResultRaw = parsedResult.Content;
                        isError = parsedResult.IsError;
                    }
                    catch (Exception ex)
                    {
                        toolResultRaw = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                    }
                });

            toolResultRaw ??= "{}";
            Trajectory.AddStep(currentStepNum, currentReasoning ?? "", toolName, argsFormatted, toolResultRaw, isError);

            Messages.Add(new ChatMessage("tool", toolResultRaw, toolName, ToolCallId: toolCall.Id));
            turnIterations++;
        }

        stopwatch.Stop();
        _tui.RenderFinalSummary(success, turnIterations <= maxIterations ? turnIterations : maxIterations, toolCounts, stopwatch.Elapsed);

        return success ? 0 : 1;
    }

    private void ExtractYamlFromContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var match = Regex.Match(content, @"```yaml\s*(?<yaml>[\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var extracted = match.Groups["yaml"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted) && (extracted.Contains("input:") || extracted.Contains("output:")))
            {
                Trajectory.LastGeneratedYaml = extracted;
            }
        }
    }
}
