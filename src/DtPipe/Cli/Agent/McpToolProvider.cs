using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DtPipe.Cli.Mcp;

namespace DtPipe.Cli.Agent;

public class McpToolProvider : IAgentToolProvider
{
    private readonly object _toolsInstance;
    private readonly Type _toolsType;
    private readonly List<ToolDefinition> _definitions;

    public McpToolProvider(object toolsInstance)
    {
        _toolsInstance = toolsInstance;
        _toolsType = toolsInstance.GetType();
        _definitions = McpToolReflector.BuildToolDefinitions(_toolsType);
    }

    public List<ToolDefinition> GetToolDefinitions() => _definitions;

    public async Task<ToolResult> InvokeToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var rawResult = await McpToolReflector.InvokeToolAsync(_toolsInstance, toolName, args, ct);
        return ToolResult.FromJson(rawResult);
    }
}
