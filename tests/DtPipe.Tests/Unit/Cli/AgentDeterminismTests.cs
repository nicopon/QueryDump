using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DtPipe.Cli.Agent;
using Moq;
using Spectre.Console;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

public class AgentDeterminismTests
{
     /// <summary>An empty, non-executing tool provider for planning-only runs.</summary>
    private sealed class EmptyToolProvider : IAgentToolProvider
    {
        public List<ToolDefinition> GetToolDefinitions() => new();
        public List<ToolDefinition> GetToolDefinitions(AgentMode mode) => new();
        public Task<ToolResult> InvokeToolAsync(string toolName, System.Text.Json.JsonElement args, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("{}"));
    }

    /// <summary>
    /// A scripting fake that records the temperature/seed it was called with and returns a
    /// fixed response (optionally carrying a YAML block in its content so the trajectory picks
    /// up a generated plan).
    /// </summary>
     private sealed class ScriptedLlmClient : ILlmClient
     {
        public double? LastTemperature { get; private set; }
        public int? LastSeed { get; private set; }
        public int CallCount { get; private set; }

        private readonly string? _content;

        public ScriptedLlmClient(string? content = null)
         {
            _content = content;
         }

        public string ProviderName => "scripted";

        public Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct = default)
             => Task.FromResult(new List<string>());

        public Task<LlmResponse> ChatAsync(
            string baseUrl,
            string model,
            List<ChatMessage> messages,
            List<ToolDefinition> tools,
            int maxTokens = 16384,
            double temperature = 0.7,
            int? seed = null,
            CancellationToken ct = default)
         {
            LastTemperature = temperature;
            LastSeed = seed;
            CallCount++;
            var response = new LlmResponse(new ChatMessage("assistant", _content ?? "done"), true, null);
            return Task.FromResult(response);
          }
     }

    private static AgentExecutor BuildExecutor(ScriptedLlmClient llm)
       {
           // A non-interactive Spectre console keeps the run headless under `dotnet test`.
        IAnsiConsole console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings());
        var tui = new AgentTui(console);
        return new AgentExecutor(new EmptyToolProvider(), llm, tui, console);
       }

    [Fact]
    public async Task Temperature_And_Seed_Are_Propagated_To_LlmClient()
    {
        var llm = new ScriptedLlmClient(content: "done");
        var executor = BuildExecutor(llm);

        var options = new AgentOptions { Temperature = 0.0, Seed = 42 };
        await executor.RunTurnAsync("mission", "model", "http://localhost:11434", options, maxIterations: 1);

        Assert.Equal(0.0, llm.LastTemperature);
        Assert.Equal(42, llm.LastSeed);
    }

    [Fact]
    public async Task Repeat_Replicates_The_Planning_Loop()
    {
        var llm = new ScriptedLlmClient(content: "done");
        var executor = BuildExecutor(llm);

        var options = new AgentOptions { Repeat = 3 };
        await executor.RunTurnAsync("mission", "model", "http://localhost:11434", options, maxIterations: 1);

        // 1 primary run + 2 replications = 3 LLM invocations.
        Assert.Equal(3, llm.CallCount);
        Assert.NotNull(executor.Trajectory.Determinism);
        Assert.Equal(3, executor.Trajectory.Determinism!.Repetitions);
    }

    [Fact]
    public async Task Repeat_Without_Yaml_Reports_Zero_Variance()
    {
        var llm = new ScriptedLlmClient(content: "planning complete, no yaml");
        var executor = BuildExecutor(llm);

        var options = new AgentOptions { Repeat = 3 };
        await executor.RunTurnAsync("mission", "model", "http://localhost:11434", options, maxIterations: 1);

        var report = executor.Trajectory.Determinism!;
        Assert.Equal(3, report.Repetitions);
        // No YAML observed in any run => still a single (empty) distinct payload => deterministic.
        Assert.Equal(0, report.Variance);
        Assert.True(report.IsDeterministic);
    }

     [Fact]
    public async Task Repeat_With_Identical_Yaml_Blocks_Reports_Zero_Variance()
      {
        string yaml = "input: csv:a.csv\noutput: csv:b.csv\n";
        string content = "here is the plan:\n" + "```yaml\n" + yaml + "```\n";
        var llm = new ScriptedLlmClient(content: content);
        var executor = BuildExecutor(llm);

        var options = new AgentOptions { Repeat = 3 };
        await executor.RunTurnAsync("mission", "model", "http://localhost:11434", options, maxIterations: 1);

        var report = executor.Trajectory.Determinism!;
        Assert.Equal(3, report.Repetitions);
        Assert.Single(report.DistinctYaml);
        Assert.Equal(0, report.Variance);
        Assert.True(report.IsDeterministic);
      }
}
