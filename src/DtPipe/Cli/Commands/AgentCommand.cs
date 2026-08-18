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

        var providerOption = new Option<string>("--provider")
        {
            Description = "LLM provider to use ('ollama' or 'openai')"
        };
        providerOption.DefaultValueFactory = _ => "ollama";

        var apiKeyOption = new Option<string?>("--api-key")
        {
            Description = "API key for OpenAI provider. Falls back to DTPIPE_LLM_API_KEY environment variable."
        };

        var modelOption = new Option<string?>("--model")
        {
            Description = "Model name (e.g. 'qwen2.5-coder:7b', 'gpt-4o'). Auto-discovered if omitted."
        };
        modelOption.Aliases.Add("-m");

        var urlOption = new Option<string?>("--url")
        {
            Description = "API endpoint URL (defaults: http://localhost:11434 for ollama, https://api.openai.com for openai)"
        };
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

        var temperatureOption = new Option<double>("--temperature")
          {
            Description = "Sampling temperature. 0 makes decoding deterministic (default)."
          };
        temperatureOption.DefaultValueFactory = _ => 0.0;

        var seedOption = new Option<int?>("--seed")
          {
            Description = "Fixed seed for reproducible sampling. A fixed seed makes the run deterministic."
          };
        seedOption.DefaultValueFactory = _ => 0;

        var repeatOption = new Option<int>("--repeat")
           {
            Description = "Number of replications of the validated plan for determinism/variance measurement (default: 1)."
           };
        repeatOption.DefaultValueFactory = _ => 1;

        var sequentialOption = new Option<bool>("--sequential")
           {
            Description = "Execute tool calls one at a time instead of running independent calls in parallel (default: parallel)."
           };
        sequentialOption.DefaultValueFactory = _ => false;

        Arguments.Add(promptArgument);
        Options.Add(promptOption);
        Options.Add(providerOption);
        Options.Add(apiKeyOption);
        Options.Add(modelOption);
        Options.Add(urlOption);
        Options.Add(maxIterOption);
        Options.Add(interactiveOption);
        Options.Add(temperatureOption);
        Options.Add(seedOption);
        Options.Add(repeatOption);
        Options.Add(sequentialOption);

        this.SetAction(async (parseResult, ct) =>
        {
            var console = serviceProvider.GetRequiredService<IAnsiConsole>();
            var mcpTools = serviceProvider.GetRequiredService<DtPipeMcpTools>();

            var prompt = parseResult.GetValue(promptArgument) ?? parseResult.GetValue(promptOption);
            var provider = parseResult.GetValue(providerOption) ?? "ollama";
            var apiKey = parseResult.GetValue(apiKeyOption);
            var model = parseResult.GetValue(modelOption);
            var url = parseResult.GetValue(urlOption);
            var maxIterations = parseResult.GetValue(maxIterOption);
            var temperature = parseResult.GetValue(temperatureOption);
            var seed = parseResult.GetValue(seedOption);
            var repeat = parseResult.GetValue(repeatOption);
            var sequential = parseResult.GetValue(sequentialOption);

            if (string.IsNullOrWhiteSpace(url))
            {
                url = provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.openai.com"
                    : "http://localhost:11434";
            }

            var tui = new AgentTui(console);
            ILlmClient llmClient = provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                ? new OpenAiClient(apiKey)
                : new OllamaClient();

            if (string.IsNullOrWhiteSpace(model))
            {
                model = await tui.SelectModelAsync(llmClient, url);
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

            var toolProvider = new McpToolProvider(mcpTools);
            var executor = new AgentExecutor(toolProvider, llmClient, tui, console);

             var agentOptions = new AgentOptions
                    {
                 Temperature = temperature,
                 Seed = seed,
                 Repeat = repeat,
                 Sequential = sequential
                    };

              // Execute initial turn
             var exitCode = await executor.RunTurnAsync(prompt, model, url, agentOptions, maxIterations, ct);

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
                             exitCode = await executor.RunTurnAsync(followUp, model, url, agentOptions, maxIterations, ct);
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
