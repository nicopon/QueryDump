using System;
using System.CommandLine;
using System.Threading.Tasks;
using DtPipe.Cli.Agent;
using DtPipe.Cli.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace DtPipe.Cli.Commands;

public class AgentCommand : Command
{
    public AgentCommand(IServiceProvider serviceProvider) 
        : base("agent", "Start an interactive or automated ReAct AI agent loop for data integration tasks")
    {
        var promptArgument = new Argument<string?>("prompt")
        {
            Description = "The data integration task description (e.g. 'Inspect csv:invoices.csv and anonymize email')",
            Arity = ArgumentArity.ZeroOrOne
        };

        var promptOption = new Option<string?>("--prompt")
        {
            Description = "The data integration task description"
        };
        promptOption.Aliases.Add("-p");

        var modelOption = new Option<string?>("--model")
        {
            Description = "Ollama model name (e.g. 'gemma4:12b-mlx', 'qwen2.5-coder:7b'). Auto-discovered if omitted."
        };
        modelOption.Aliases.Add("-m");

        var urlOption = new Option<string>("--url")
        {
            Description = "Ollama API endpoint URL"
        };
        urlOption.DefaultValueFactory = _ => "http://localhost:11434";
        urlOption.Aliases.Add("-u");

        var maxIterOption = new Option<int>("--max-iterations")
        {
            Description = "Maximum ReAct loop iterations per turn"
        };
        maxIterOption.DefaultValueFactory = _ => 25;

        var interactiveOption = new Option<bool>("--interactive")
        {
            Description = "Force interactive mode for model selection and task prompt"
        };
        interactiveOption.Aliases.Add("-i");

        Arguments.Add(promptArgument);
        Options.Add(promptOption);
        Options.Add(modelOption);
        Options.Add(urlOption);
        Options.Add(maxIterOption);
        Options.Add(interactiveOption);

        this.SetAction(async (parseResult, ct) =>
        {
            var console = serviceProvider.GetRequiredService<IAnsiConsole>();
            var mcpTools = serviceProvider.GetRequiredService<DtPipeMcpTools>();

            var prompt = parseResult.GetValue(promptArgument) ?? parseResult.GetValue(promptOption);
            var model = parseResult.GetValue(modelOption);
            var url = parseResult.GetValue(urlOption) ?? "http://localhost:11434";
            var maxIterations = parseResult.GetValue(maxIterOption);

            var tui = new AgentTui(console);
            var ollamaClient = new OllamaClient();

            if (string.IsNullOrWhiteSpace(model))
            {
                model = await tui.SelectModelAsync(ollamaClient, url);
                if (string.IsNullOrWhiteSpace(model))
                {
                    console.MarkupLine("[red]Error:[/] No model selected.");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = tui.PromptUserMission();
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    console.MarkupLine("[red]Error:[/] Mission prompt cannot be empty.");
                    return;
                }
            }

            tui.RenderHeader(model, url);
            var executor = new AgentExecutor(mcpTools, ollamaClient, tui, console);

            // Execute initial turn
            var exitCode = await executor.RunTurnAsync(prompt, model, url, maxIterations, ct);

            // Interactive post-mission conversation loop
            while (!ct.IsCancellationRequested)
            {
                bool hasYaml = !string.IsNullOrEmpty(executor.Trajectory.LastGeneratedYaml);
                var action = tui.ShowPostMissionMenu(hasYaml);

                if (action == PostMissionAction.Exit)
                {
                    break;
                }

                switch (action)
                {
                    case PostMissionAction.ContinueDiscussion:
                        var followUp = tui.PromptFollowUp();
                        if (!string.IsNullOrWhiteSpace(followUp))
                        {
                            exitCode = await executor.RunTurnAsync(followUp, model, url, maxIterations, ct);
                        }
                        break;

                    case PostMissionAction.ViewDag:
                        if (executor.Trajectory.LastGeneratedYaml != null)
                        {
                            tui.RenderPipelineDag(executor.Trajectory.LastGeneratedYaml, serviceProvider);
                        }
                        break;

                    case PostMissionAction.InspectTrajectory:
                        tui.InspectTrajectory(executor.Trajectory);
                        break;

                    case PostMissionAction.SaveYaml:
                        if (executor.Trajectory.LastGeneratedYaml != null)
                        {
                            tui.SaveYamlToFile(executor.Trajectory.LastGeneratedYaml);
                        }
                        break;
                }
            }

            if (exitCode != 0)
            {
                Environment.ExitCode = exitCode;
            }
        });
    }
}
