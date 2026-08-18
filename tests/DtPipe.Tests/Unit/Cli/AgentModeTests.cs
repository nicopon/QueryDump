using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DtPipe.Cli.Agent;
using ModelContextProtocol.Server;
using Spectre.Console;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

/// <summary>
/// F1 — planner/executor split: in PLAN mode the execution tool is never offered to the LLM, and
/// the planner prompt is selected; execution stays a deterministic engine step.
/// </summary>
public class AgentModeTests
{
     /// <summary>
       /// A tools class that mirrors the real MCP surface: a planning tool and the execution tool
       /// '<c>execute-yaml-job</c>' that must be filtered out in PLAN mode.
       /// </summary>
    private sealed class PlanAndExecuteTools
      {
         [McpServerTool(Name = "validate-yaml-job")]
         [System.ComponentModel.Description("Validate a YAML job.")]
         public string ValidateYamlJob(string yamlContent) => "ok";

        [McpServerTool(Name = "execute-yaml-job")]
        [System.ComponentModel.Description("Execute a YAML job.")]
        public string ExecuteYamlJob(string yamlContent) => "ran";
        }

     [Fact]
    public void Plan_Mode_Excludes_Execution_Tool_But_KeePs_Planning_Tools()
      {
        var provider = new McpToolProvider(new PlanAndExecuteTools());

        var planTools = provider.GetToolDefinitions(AgentMode.Plan);
        Assert.DoesNotContain(planTools, t => t.Name == "execute-yaml-job");
        Assert.Contains(planTools, t => t.Name == "validate-yaml-job");
        }

      [Fact]
    public void Execute_Mode_Includes_Execution_Tool()
      {
        var provider = new McpToolProvider(new PlanAndExecuteTools());

        var execTools = provider.GetToolDefinitions(AgentMode.Execute);
        Assert.Contains(execTools, t => t.Name == "execute-yaml-job");
        }

      [Fact]
    public void Autonomous_Mode_Includes_Execution_Tool()
      {
        var provider = new McpToolProvider(new PlanAndExecuteTools());

        var autoTools = provider.GetToolDefinitions(AgentMode.Autonomous);
        Assert.Contains(autoTools, t => t.Name == "execute-yaml-job");
        }

     [Fact]
    public void ToolModePolicy_Blocks_Execution_Tool_In_Plan_Mode_Only()
        {
            // 'execute-yaml-job' is the only execution tool and must be blocked in PLAN mode.
        Assert.True(ToolModePolicy.IsBlockedInPlanMode("execute-yaml-job"));
        Assert.True(ToolModePolicy.IsExecutionTool("execute-yaml-job"));
         // A planning tool is never blocked.
        Assert.False(ToolModePolicy.IsBlockedInPlanMode("validate-yaml-job"));
        Assert.False(ToolModePolicy.IsExecutionTool("validate-yaml-job"));
          }

       [Fact]
    public void Select_Plan_Mode_Uses_Planner_Prompt_Forbidding_Execution()
         {
        var plan = AgentSystemPrompt.Select(AgentMode.Plan);
        Assert.Contains("PLANNER", plan);
           // The planner prompt must explicitly forbid execution.
        Assert.Contains("execute-yaml-job", plan);
        Assert.Contains("FORBIDDEN", plan);
         }

        [Fact]
    public void Select_Execute_And_Autonomous_Uses_Executor_Prompt()
            {
            Assert.Contains("EXECUTOR", AgentSystemPrompt.Select(AgentMode.Execute));
            Assert.Contains("EXECUTOR", AgentSystemPrompt.Select(AgentMode.Autonomous));
             }
}
