using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

/// <summary>
/// Abstraction for providing tools to the agent.
/// Allows decoupling the agent execution loop from DtPipeMcpTools.
/// </summary>
public interface IAgentToolProvider
{
    List<ToolDefinition> GetToolDefinitions();
    Task<ToolResult> InvokeToolAsync(string toolName, JsonElement args, CancellationToken ct);
}
