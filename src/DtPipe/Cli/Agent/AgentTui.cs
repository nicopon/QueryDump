using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DtPipe.Cli.Pipeline;
using DtPipe.Configuration;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Pipelines.Dag;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace DtPipe.Cli.Agent;

public enum PostMissionAction
{
    ContinueDiscussion,
    ViewDag,
    InspectTrajectory,
    SaveYaml,
    Exit
}

public class AgentTui
{
    private readonly IAnsiConsole _console;

    public AgentTui(IAnsiConsole console)
    {
        _console = console;
    }

    public void RenderHeader(string model, string url)
    {
        var rule = new Rule("[bold cyan]dtpipe AI Agent[/]")
        {
            Justification = Justify.Left
        };
        _console.Write(rule);
        _console.MarkupLine($"[grey]Model:[/] [bold green]{Markup.Escape(model)}[/]  |  [grey]Endpoint:[/] [blue]{Markup.Escape(url)}[/]");
        _console.WriteLine();
    }

    public async Task<string?> SelectModelAsync(ILlmClient llmClient, string url)
    {
        var models = await llmClient.ListModelsAsync(url);
        if (models.Count == 0)
        {
            _console.MarkupLine($"[yellow]⚠️ Could not auto-discover {llmClient.ProviderName} models at endpoint.[/]");
            string defaultModel = llmClient.ProviderName == "ollama" ? "gemma4:12b-mlx" : "gpt-4o";
            var manualModel = _console.Prompt(
                new TextPrompt<string>($"Enter {llmClient.ProviderName} model name (e.g., [green]{defaultModel}[/]):")
                    .DefaultValue(defaultModel)
            );
            return manualModel;
        }

        var prompt = new SelectionPrompt<string>()
            .Title($"Select a [bold cyan]{llmClient.ProviderName} Model[/] for the mission:")
            .PageSize(10)
            .MoreChoicesText("[grey](Move up and down to reveal more models)[/]");

        foreach (var m in models.OrderBy(m => m))
        {
            prompt.AddChoice(m);
        }

        return _console.Prompt(prompt);
    }

    public string PromptUserMission()
    {
        return _console.Prompt(
            new TextPrompt<string>("Describe your [bold cyan]data integration task[/]:")
                .PromptStyle("yellow")
        );
    }

    public string PromptFollowUp()
    {
        _console.WriteLine();
        return _console.Prompt(
            new TextPrompt<string>("💬 [bold cyan]Follow-up prompt or question[/]:")
                .PromptStyle("yellow")
        );
    }

    public void RenderAgentResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        _console.WriteLine();
        var panel = new Panel(Markup.Escape(content))
        {
            Header = new PanelHeader("[bold green]🤖 dtpipe Agent[/]"),
            Border = BoxBorder.Rounded,
            Expand = true
        };
        _console.Write(panel);
    }

     public void RenderCompactIterationStatus(int iteration, int maxIterations, string? reasoning, string? toolName)
    {
        string toolPart = !string.IsNullOrEmpty(toolName) ? $" → [magenta]{Markup.Escape(toolName)}[/]" : "";
        string reasoningSnippet = "";
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            var firstLine = reasoning.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (firstLine.Length > 70) firstLine = firstLine[..67] + "...";
            reasoningSnippet = $": [grey]{Markup.Escape(firstLine)}[/]";
        }

        _console.MarkupLine($"[dim][[Step {iteration}/{maxIterations}]][/]{toolPart}{reasoningSnippet}");
    }

    public void InspectTrajectory(AgentTrajectory trajectory)
    {
        if (trajectory.Steps.Count == 0)
        {
            _console.MarkupLine("[yellow]No trajectory steps logged in this session yet.[/]");
            return;
        }

        while (true)
        {
            _console.WriteLine();
            var prompt = new SelectionPrompt<string>()
                .Title("🧠 [bold cyan]Trajectory Step Inspector[/] (Select a step to view details):")
                .PageSize(12);

            var stepMap = new Dictionary<string, TrajectoryStep>();
            for (int i = 0; i < trajectory.Steps.Count; i++)
            {
                var s = trajectory.Steps[i];
                string statusIcon = s.IsError ? "⚠️" : (s.ToolName != null ? "🛠" : "💭");
                string label = $"{statusIcon} Step {s.Iteration} [[{s.Timestamp:HH:mm:ss}]]: {Markup.Escape(s.ToolName ?? "Reasoning")}";
                stepMap[label] = s;
                prompt.AddChoice(label);
            }

            const string backOption = "⬅ Back to main menu";
            prompt.AddChoice(backOption);

            var selected = _console.Prompt(prompt);
            if (selected == backOption)
                break;

            var step = stepMap[selected];
            RenderStepDetails(step);
        }
    }

    private void RenderStepDetails(TrajectoryStep step)
    {
        _console.WriteLine();
        var rule = new Rule($"[bold blue]Step {step.Iteration} Details ({step.Timestamp:HH:mm:ss})[/]")
        {
            Justification = Justify.Left
        };
        _console.Write(rule);

        if (!string.IsNullOrWhiteSpace(step.Reasoning))
        {
            var reasoningPanel = new Panel(Markup.Escape(step.Reasoning))
            {
                Header = new PanelHeader("[yellow]Agent Reasoning / Intent[/]"),
                Border = BoxBorder.Rounded,
                Expand = true
            };
            _console.Write(reasoningPanel);
        }

        if (!string.IsNullOrEmpty(step.ToolName))
        {
            _console.MarkupLine($"[bold magenta]Tool Executed:[/] {Markup.Escape(step.ToolName)}");
            if (!string.IsNullOrWhiteSpace(step.ToolArgs))
            {
                var argsPanel = new Panel(Markup.Escape(step.ToolArgs))
                {
                    Header = new PanelHeader("[grey]Tool Arguments[/]"),
                    Border = BoxBorder.Square,
                    Expand = true
                };
                _console.Write(argsPanel);
            }

            if (!string.IsNullOrWhiteSpace(step.ToolResult))
            {
                string color = step.IsError ? "red" : "green";
                var resultPanel = new Panel(Markup.Escape(step.ToolResult))
                {
                    Header = new PanelHeader($"[bold {color}]Tool Output[/]"),
                    Border = BoxBorder.Rounded,
                    Expand = true
                };
                _console.Write(resultPanel);
            }
        }
    }

     /// <summary>
      /// Renders a single tool result (F5 parallel tool execution). Kept lightweight so that many
      /// independent calls produced in one turn can be rendered without heavy panels per call.
      /// </summary>
     public void RenderToolResult(string toolName, string result, bool isError)
        {
         string color = isError ? "red" : "green";
         string snippet = result ?? "{}";
         if (snippet.Length > 200) snippet = snippet[..200] + "…";
          _console.MarkupLine($"[dim]↳ {Markup.Escape(toolName)}{Markup.Escape((isError ? " [error]" : ""))}[/]: [bold {color}]{Markup.Escape(snippet)}[/]");
        }

     public void RenderPipelineDag(string yamlContent, IServiceProvider serviceProvider)
    {
        try
        {
            var secretsManager = serviceProvider.GetService<DtPipe.Cli.Security.ISecretsManager>();
            var jobs = JobFileParser.ParseContent(yamlContent, secretsManager);
            var streamTransformerFactories = serviceProvider.GetRequiredService<IEnumerable<IStreamTransformerFactory>>();
            var readerFactories = serviceProvider.GetRequiredService<IEnumerable<IStreamReaderFactory>>();

            var branches = jobs.Select(kv => new BranchDefinition
            {
                Alias = kv.Key,
                Input = kv.Value.Input,
                Output = kv.Value.Output,
                StreamingAliases = kv.Value.From != null
                    ? kv.Value.From.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>(),
                RefAliases = kv.Value.Ref ?? Array.Empty<string>(),
                Arguments = Array.Empty<string>(),
                ProcessorName = streamTransformerFactories
                    .FirstOrDefault(f => f.IsApplicable(kv.Value))
                    ?.ComponentName,
                PreParsedJob = kv.Value
            }).ToList();

            var dag = new JobDagDefinition { Branches = branches };

            _console.WriteLine();
            if (dag.Branches.Count > 1)
            {
                _console.Write(DagRenderer.BuildTopologyPanel(dag, readerFactories));
            }
            else if (dag.Branches.Count == 1)
            {
                _console.Write(DagRenderer.BuildLinearTopologyPanel(jobs.Values.First(), readerFactories));
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Could not render DAG topology:[/] {Markup.Escape(ex.Message)}");
        }
    }

    public void SaveYamlToFile(string yamlContent)
    {
        var filePath = _console.Prompt(
            new TextPrompt<string>("Enter file path to save the YAML pipeline:")
                .DefaultValue("pipeline.yaml")
        );

        try
        {
            File.WriteAllText(filePath, yamlContent);
            _console.MarkupLine($"[bold green]✓ Pipeline configuration saved to '[white]{Markup.Escape(filePath)}[/]'[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[bold red]❌ Failed to save file:[/] {Markup.Escape(ex.Message)}");
        }
    }

    public PostMissionAction ShowPostMissionMenu(bool hasYaml)
    {
        _console.WriteLine();
        var prompt = new SelectionPrompt<string>()
            .Title("What would you like to do next?")
            .PageSize(10);

        const string continueOpt = "💬 Continue discussion / Refine pipeline";
        const string viewDagOpt = "📊 View Pipeline DAG Topology";
        const string inspectOpt = "🧠 Inspect full step-by-step trajectory";
        const string saveYamlOpt = "💾 Save pipeline YAML file to disk";
        const string exitOpt = "🚪 Exit";

        prompt.AddChoice(continueOpt);
        if (hasYaml) prompt.AddChoice(viewDagOpt);
        prompt.AddChoice(inspectOpt);
        if (hasYaml) prompt.AddChoice(saveYamlOpt);
        prompt.AddChoice(exitOpt);

        var selected = _console.Prompt(prompt);

        return selected switch
        {
            continueOpt => PostMissionAction.ContinueDiscussion,
            viewDagOpt => PostMissionAction.ViewDag,
            inspectOpt => PostMissionAction.InspectTrajectory,
            saveYamlOpt => PostMissionAction.SaveYaml,
            _ => PostMissionAction.Exit
        };
    }

    public void RenderFinalSummary(bool success, int iterations, Dictionary<string, int> toolCounts, TimeSpan duration)
    {
        _console.WriteLine();
        var rule = new Rule("[bold cyan]Session Status[/]")
        {
            Justification = Justify.Left
        };
        _console.Write(rule);

        var table = new Table().Expand();
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Value[/]");

        string statusMarkup = success ? "[bold green]🟢 COMPLETED[/]" : "[bold red]❌ INCOMPLETE[/]";
        table.AddRow("Status", statusMarkup);
        table.AddRow("Iterations", iterations.ToString());
        table.AddRow("Duration", $"{duration.TotalSeconds:F2} seconds");

        if (toolCounts.Count > 0)
        {
            var toolSummary = string.Join(", ", toolCounts.Select(kv => $"{kv.Key}: {kv.Value}"));
            table.AddRow("Tool Calls", Markup.Escape(toolSummary));
        }

        _console.Write(table);
    }
}
