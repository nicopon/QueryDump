using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using DtPipe.Cli.Mcp;
using DtPipe.Core.Security;
using DtPipe.Core.Abstractions;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

public class McpToolsTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DtPipeMcpTools _tools;
    private readonly IMcpHelpService _helpService;

    public McpToolsTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DtPipe.Core.Options.OptionsRegistry>();
        services.AddSingleton<IEnumerable<IStreamTransformerFactory>>(Array.Empty<IStreamTransformerFactory>());
        _serviceProvider = services.BuildServiceProvider();

        _helpService = new McpHelpService(
            Array.Empty<IStreamReaderFactory>(),
            Array.Empty<IDataTransformerFactory>(),
            Array.Empty<IDataWriterFactory>());

        _tools = new DtPipeMcpTools(
            Array.Empty<IStreamReaderFactory>(),
            Array.Empty<IDataTransformerFactory>(),
            Array.Empty<IDataWriterFactory>(),
            _helpService,
            _serviceProvider);
    }

    private void InvokeValidatePathSafety(string path)
    {
        var method = typeof(DtPipeMcpTools).GetMethod("ValidatePathSafety", 
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        try
        {
            method.Invoke(null, new object?[] { path });
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private string[] InvokeSplitArguments(string commandLine)
    {
        var method = typeof(DtPipeMcpTools).GetMethod("SplitArguments", 
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (string[])method.Invoke(null, new object[] { commandLine })!;
    }

    [Fact]
    public void ValidatePathSafety_PathWithinCwd_Success()
    {
        var relativePath = "data.csv";
        var nestedPath = Path.Combine("subfolder", "data.parquet");
        var absoluteInCwd = Path.Combine(Directory.GetCurrentDirectory(), "data.jsonl");

        // Should not throw
        InvokeValidatePathSafety(relativePath);
        InvokeValidatePathSafety(nestedPath);
        InvokeValidatePathSafety(absoluteInCwd);
    }

    [Fact]
    public void ValidatePathSafety_PathOutsideCwd_Throws()
    {
        var absoluteOutside = "/etc/passwd";
        var relativeParentEscaped = "../outside_cwd.csv";
        var complexEscaped = "subfolder/../../outside.csv";

        Assert.Throws<UnauthorizedAccessException>(() => InvokeValidatePathSafety(absoluteOutside));
        Assert.Throws<UnauthorizedAccessException>(() => InvokeValidatePathSafety(relativeParentEscaped));
        Assert.Throws<UnauthorizedAccessException>(() => InvokeValidatePathSafety(complexEscaped));
    }

    [Theory]
    [InlineData("Host=localhost;Database=mydb;Username=postgres;Password=123;")]
    [InlineData("Server=myServer;Database=db;User Id=uid;Password=pwd;")]
    [InlineData("sqlite:Host=dummy;Database=ignored;")]
    [InlineData(":memory:")]
    [InlineData("-")]
    public void ValidatePathSafety_DbConnectionStringOrSpecial_SkipsCheck(string path)
    {
        // Should not throw even though it doesn't represent a valid file path inside CWD
        InvokeValidatePathSafety(path);
    }

    [Fact]
    public void SplitArguments_SimpleAndQuotes_ParsedCorrectly()
    {
        var command = "dtpipe -i file.csv --sql \"SELECT * FROM table\" -o out.parquet";
        var expected = new[] { "dtpipe", "-i", "file.csv", "--sql", "SELECT * FROM table", "-o", "out.parquet" };

        var result = InvokeSplitArguments(command);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void McpSecurityContext_StateChange_Works()
    {
        IMcpSecurityContext context = new McpSecurityContext();
        Assert.False(context.IsMcpSession);
        
        context.IsMcpSession = true;
        Assert.True(context.IsMcpSession);
        
        context.IsMcpSession = false;
        Assert.False(context.IsMcpSession);
    }

    [Fact]
    public void Help_ReturnsGeneralHelpContent()
    {
        var result = _tools.Help();
        Assert.Contains("dtpipe — Data streaming & anonymization engine", result);
        Assert.Contains("YAML JOB USAGE", result);
        Assert.Contains("ADAPTERS:", result);
    }

    [Fact]
    public void ValidateYamlJob_EmptyYaml_ReturnsError()
    {
        var json = _tools.ValidateYamlJob("");
        Assert.Contains("YAML job content cannot be empty", json);
    }

    [Fact]
    public void ValidateYamlJob_ValidYaml_ReturnsSuccess()
    {
        var yaml = @"
main:
  input: ""csv:input.csv""
  output: ""csv:output.csv""
";
        var json = _tools.ValidateYamlJob(yaml);
        Assert.Contains("\"success\": true", json);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExecuteYamlJob_EmptyYaml_ReturnsError()
    {
        var json = await _tools.ExecuteYamlJob("");
        Assert.Contains("YAML job content cannot be empty", json);
    }

    [Fact]
    public void GetAdapterHelp_UnknownAdapter_ReturnsError()
    {
        var json = _tools.GetAdapterHelp("nonexistent_adapter");
        Assert.Contains("Unknown adapter", json);
        Assert.Contains("nonexistent_adapter", json);
    }

    [Fact]
    public void GetTransformerHelp_UnknownTransformer_ReturnsError()
    {
        var json = _tools.GetTransformerHelp("nonexistent_transformer");
        Assert.Contains("Unknown transformer", json);
        Assert.Contains("nonexistent_transformer", json);
    }
}
