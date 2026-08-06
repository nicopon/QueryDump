using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DtPipe.Cli.Agent;
using DtPipe.Cli.Mcp;
using DtPipe.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

public class DummyMcpTools
{
    [McpServerTool(Name = "sample-tool")]
    [System.ComponentModel.Description("A sample tool for testing")]
    public string SampleTool(
        [System.ComponentModel.Description("Sample input path")] string input,
        [System.ComponentModel.Description("Optional limit")] int? limit = 5)
    {
        return JsonSerializer.Serialize(new { input, limit });
    }

    [McpServerTool(Name = "async-tool")]
    [System.ComponentModel.Description("An async tool for testing")]
    public async Task<string> AsyncTool(
        [System.ComponentModel.Description("Input message")] string message,
        CancellationToken ct = default)
    {
        await Task.Yield();
        return JsonSerializer.Serialize(new { echoed = message });
    }
}

public class McpToolReflectorTests
{
    [Fact]
    public void BuildToolDefinitions_ReflectsToolsCorrectly()
    {
        var tools = McpToolReflector.BuildToolDefinitions(typeof(DummyMcpTools));
        Assert.Equal(2, tools.Count);

        var sampleTool = tools.FirstOrDefault(t => t.Function.Name == "sample-tool");
        Assert.NotNull(sampleTool);
        Assert.Equal("A sample tool for testing", sampleTool.Function.Description);

        var props = sampleTool.Function.Parameters.GetProperty("properties");
        Assert.True(props.TryGetProperty("input", out var inputProp));
        Assert.Equal("string", inputProp.GetProperty("type").GetString());
        Assert.Equal("Sample input path", inputProp.GetProperty("description").GetString());

        Assert.True(props.TryGetProperty("limit", out var limitProp));
        Assert.Equal("integer", limitProp.GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvokeToolAsync_SyncMethod_InvokesCorrectly()
    {
        var dummy = new DummyMcpTools();
        using var argsDoc = JsonDocument.Parse(@"{ ""input"": ""test.csv"", ""limit"": 10 }");
        
        var resultJson = await McpToolReflector.InvokeToolAsync(dummy, "sample-tool", argsDoc.RootElement, CancellationToken.None);
        
        Assert.Contains("test.csv", resultJson);
        Assert.Contains("10", resultJson);
    }

    [Fact]
    public async Task InvokeToolAsync_AsyncMethod_InvokesCorrectly()
    {
        var dummy = new DummyMcpTools();
        using var argsDoc = JsonDocument.Parse(@"{ ""message"": ""hello world"" }");

        var resultJson = await McpToolReflector.InvokeToolAsync(dummy, "async-tool", argsDoc.RootElement, CancellationToken.None);

        Assert.Contains("hello world", resultJson);
    }

    [Fact]
    public async Task InvokeToolAsync_UnknownTool_ReturnsErrorJson()
    {
        var dummy = new DummyMcpTools();
        using var argsDoc = JsonDocument.Parse(@"{}");

        var resultJson = await McpToolReflector.InvokeToolAsync(dummy, "nonexistent", argsDoc.RootElement, CancellationToken.None);

        Assert.Contains("Unknown tool", resultJson);
        Assert.True(ToolResult.FromJson(resultJson).IsError);
    }
}
