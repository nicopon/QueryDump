using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

/// <summary>
/// Abstraction pour la fourniture de tools à l'agent.
/// Permet de découpler l'agent de DtPipeMcpTools.
/// </summary>
public interface IAgentToolProvider
{
    List<ToolDefinition> GetToolDefinitions();
    Task<ToolResult> InvokeToolAsync(string toolName, JsonElement args, CancellationToken ct);
}
