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
    private readonly DtPipeMcpTools _mcpTools;
    private readonly OllamaClient _ollamaClient;
    private readonly AgentTui _tui;
    private readonly IAnsiConsole _console;

    public AgentTrajectory Trajectory { get; } = new();
    public List<OllamaClient.OllamaChatMessage> Messages { get; } = new();

    public AgentExecutor(DtPipeMcpTools mcpTools, OllamaClient ollamaClient, AgentTui tui, IAnsiConsole console)
    {
        _mcpTools = mcpTools;
        _ollamaClient = ollamaClient;
        _tui = tui;
        _console = console;

        Messages.Add(new OllamaClient.OllamaChatMessage("system", AgentSystemPrompt.DefaultSystemPrompt));
    }

    public async Task<int> RunTurnAsync(
        string userPrompt,
        string model,
        string baseUrl,
        int maxIterations = 25,
        CancellationToken ct = default)
    {
        Messages.Add(new OllamaClient.OllamaChatMessage("user", userPrompt));

        var tools = GetToolDefinitions();
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();
        bool success = false;
        int turnIterations = 1;

        while (turnIterations <= maxIterations)
        {
            int currentStepNum = Trajectory.Steps.Count + 1;
            string? currentReasoning = null;
            string? currentToolName = null;

            OllamaClient.ChatResponse response;

            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue bold"))
                .StartAsync($"Agent thinking (Step {currentStepNum})...", async ctx =>
                {
                    response = await _ollamaClient.ChatAsync(baseUrl, model, Messages, tools, 16384, ct);
                    
                    if (string.IsNullOrEmpty(response.Error))
                    {
                        var message = response.Message;
                        Messages.Add(message);
                        currentReasoning = message.Content;

                        ExtractYamlFromContent(currentReasoning);

                        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                        {
                            var toolCall = message.ToolCalls[0];
                            currentToolName = toolCall.Function.Name;
                        }
                    }
                });

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
            var fn = toolCall.Function;
            var toolName = fn.Name;
            var args = fn.Arguments;

            toolCounts[toolName] = toolCounts.GetValueOrDefault(toolName, 0) + 1;
            string argsFormatted = args.ValueKind != JsonValueKind.Undefined ? args.ToString() : "{}";

            // Extract YAML if arguments contain yamlContent
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("yamlContent", out var yamlProp) && yamlProp.ValueKind == JsonValueKind.String)
            {
                Trajectory.LastGeneratedYaml = yamlProp.GetString();
            }

            string toolResult = "{}";
            bool isError = false;

            await _console.Status()
                .Spinner(Spinner.Known.Default)
                .SpinnerStyle(Style.Parse("magenta bold"))
                .StartAsync($"Executing tool '{toolName}'...", async ctx =>
                {
                    try
                    {
                        toolResult = await DispatchToolCallAsync(toolName, args, ct);
                        if (toolResult.Contains("\"error\"") || toolResult.Contains("\"warning\""))
                        {
                            isError = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                    }
                });

            toolResult ??= "{}";
            Trajectory.AddStep(currentStepNum, currentReasoning ?? "", toolName, argsFormatted, toolResult, isError);

            Messages.Add(new OllamaClient.OllamaChatMessage("tool", toolResult, toolName));
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

    private async Task<string> DispatchToolCallAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        return toolName.ToLowerInvariant() switch
        {
            "list-providers" => _mcpTools.ListProviders(),
            "help" => _mcpTools.Help(),
            "get-anonymization-help" => _mcpTools.GetAnonymizationHelp(),
            "get-adapter-help" => _mcpTools.GetAdapterHelp(GetArgString(args, "adapterName") ?? GetArgString(args, "name") ?? ""),
            "get-transformer-help" => _mcpTools.GetTransformerHelp(GetArgString(args, "transformerName") ?? GetArgString(args, "name") ?? ""),
            "register-yaml-job" => _mcpTools.RegisterYamlJob(GetArgString(args, "name") ?? "job", GetArgString(args, "yamlContent") ?? ""),
            "inspect" => await _mcpTools.Inspect(GetArgString(args, "input") ?? "", GetArgString(args, "query"), ct),
            "validate-yaml-job" => _mcpTools.ValidateYamlJob(GetArgString(args, "yamlContent") ?? ""),
            "execute-yaml-job" => await _mcpTools.ExecuteYamlJob(GetArgString(args, "yamlContent") ?? "", ct),
            "preview-data" => await _mcpTools.PreviewData(GetArgString(args, "input") ?? "", GetArgString(args, "query"), GetArgInt(args, "limit"), ct),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool '{toolName}'." })
        };
    }

    private static string? GetArgString(JsonElement args, string propertyName)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            return prop.ToString();
        }
        return null;
    }

    private static int? GetArgInt(JsonElement args, string propertyName)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static List<OllamaClient.ToolDefinition> GetToolDefinitions()
    {
        using var inspectParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""input"": { ""type"": ""string"", ""description"": ""Connection string or file path with provider prefix (e.g. 'csv:data.csv', 'sqlite:file.db')"" },
                ""query"": { ""type"": ""string"", ""description"": ""Optional SQL query for database sources"" }
            },
            ""required"": [""input""]
        }");

        using var validateParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""yamlContent"": { ""type"": ""string"", ""description"": ""The complete YAML configuration string representing the pipeline"" }
            },
            ""required"": [""yamlContent""]
        }");

        using var executeParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""yamlContent"": { ""type"": ""string"", ""description"": ""The complete YAML configuration string representing the pipeline"" }
            },
            ""required"": [""yamlContent""]
        }");

        using var adapterParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""adapterName"": { ""type"": ""string"", ""description"": ""Name of the adapter (e.g. 'csv', 'sqlite')"" }
            },
            ""required"": [""adapterName""]
        }");

        using var transformerParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""transformerName"": { ""type"": ""string"", ""description"": ""Name of the transformer (e.g. 'compute', 'fake', 'filter')"" }
            },
            ""required"": [""transformerName""]
        }");

        using var previewParams = JsonDocument.Parse(@"
        {
            ""type"": ""object"",
            ""properties"": {
                ""input"": { ""type"": ""string"", ""description"": ""Connection string or file path with provider prefix"" },
                ""query"": { ""type"": ""string"", ""description"": ""Optional SQL query"" },
                ""limit"": { ""type"": ""integer"", ""description"": ""Number of rows to return (default 5, max 10)"" }
            },
            ""required"": [""input""]
        }");

        using var emptyParams = JsonDocument.Parse(@"{ ""type"": ""object"", ""properties"": {} }");

        return new List<OllamaClient.ToolDefinition>
        {
            new("function", new("inspect", "Inspect the schema of a data source or database tables", inspectParams.RootElement.Clone())),
            new("validate-yaml-job", new("validate-yaml-job", "Validate a pipeline configuration specified directly as YAML", validateParams.RootElement.Clone())),
            new("execute-yaml-job", new("execute-yaml-job", "Execute a pipeline configuration specified directly as YAML", executeParams.RootElement.Clone())),
            new("list-providers", new("list-providers", "List available data source providers, writers, and transformers in dtpipe", emptyParams.RootElement.Clone())),
            new("help", new("help", "Show general usage guidelines and YAML job structures", emptyParams.RootElement.Clone())),
            new("get-adapter-help", new("get-adapter-help", "Show detailed help on a specific data adapter", adapterParams.RootElement.Clone())),
            new("get-transformer-help", new("get-transformer-help", "Show detailed help on a specific transformer", transformerParams.RootElement.Clone())),
            new("get-anonymization-help", new("get-anonymization-help", "Show detailed help on data faking (anonymization)", emptyParams.RootElement.Clone())),
            new("preview-data", new("preview-data", "Preview up to 10 rows of data from a source", previewParams.RootElement.Clone()))
        };
    }
}
