using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Options;
using DtPipe.Core.Pipelines.Dag;
using DtPipe.Configuration;
using DtPipe.Cli;
using DtPipe.Cli.Pipeline;
using DtPipe.Cli.Infrastructure;
using DtPipe.Cli.Security;
using ModelContextProtocol.Server;
namespace DtPipe.Cli.Mcp;

public partial class DtPipeMcpTools
{

    internal static string[] SplitArguments(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
            }
            else if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inDoubleQuotes && !inSingleQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        
        return args.ToArray();
    }


    private record ReaderResolutionResult(
        IStreamReaderFactory Factory,
        string EffectiveConnectionString);

    private ReaderResolutionResult? TryResolveReader(
        OptionsRegistry registry,
        List<IStreamReaderFactory> readerFactories,
        string input,
        string? query)
    {
        string effectiveConnectionString = input;
        IStreamReaderFactory? factory = null;

        foreach (var f in readerFactories)
        {
            if (input.StartsWith(f.ComponentName + ":", StringComparison.OrdinalIgnoreCase))
            {
                effectiveConnectionString = input.Substring(f.ComponentName.Length + 1);
                factory = f;
                break;
            }
        }

        if (factory == null)
        {
            foreach (var f in readerFactories)
            {
                if (f.CanHandle(input))
                {
                    factory = f;
                    break;
                }
            }
        }

        if (factory == null) return null;

        ValidatePathSafety(effectiveConnectionString);

        registry.Register(new DtPipe.Cli.Infrastructure.ConnectionRoute(effectiveConnectionString, string.Empty));
        var readerOpts = registry.Get(factory.OptionsType) as DtPipe.Core.Options.IQueryAwareOptions;
        if (readerOpts != null && !string.IsNullOrWhiteSpace(query))
            readerOpts.Query = query;

        return new ReaderResolutionResult(factory, effectiveConnectionString);
    }


    [McpServerTool(Name = "dry-run")]
    [System.ComponentModel.Description("Dry-run a pipeline YAML configuration: validates, opens the reader, reports schema and estimated row count without writing any data.")]
    public async Task<string> DryRun(
        [System.ComponentModel.Description("The complete YAML configuration string representing the pipeline")] string yamlContent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return JsonSerializer.Serialize(new { success = false, error = "YAML job content cannot be empty." });

        try
        {
            var parsed = ParseAndValidateYaml(yamlContent);

            if (parsed.Errors.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    stage = "validation",
                    errors = parsed.Errors
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var registry = _serviceProvider.GetRequiredService<OptionsRegistry>();
            var readerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamReaderFactory>>().ToList();
            var branchesInfo = new List<object>();

            foreach (var (alias, job) in parsed.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Input))
                {
                    branchesInfo.Add(new
                    {
                        branch = alias,
                        message = "Intermediate or non-source branch (no direct input connection string)."
                    });
                    continue;
                }

                string? query = null;
                if (job.ProviderOptions != null)
                {
                    foreach (var f in readerFactories)
                    {
                        if (job.Input.StartsWith(f.ComponentName + ":", StringComparison.OrdinalIgnoreCase) || f.CanHandle(job.Input))
                        {
                            query = TryGetQueryFromJob(job, f.ComponentName);
                            break;
                        }
                    }
                }

                var resolved = TryResolveReader(registry, readerFactories, job.Input, query);
                if (resolved == null)
                {
                    branchesInfo.Add(new
                    {
                        branch = alias,
                        input = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(job.Input),
                        error = "No provider found for the given input."
                    });
                    continue;
                }

                try
                {
                    await using var reader = resolved.Factory.Create(registry);
                    await reader.OpenAsync(ct);

                    var columns = reader.Columns?.Select(c => (object)new
                    {
                        c.Name,
                        Type = c.ClrType?.Name ?? "unknown",
                        c.IsNullable
                    }).ToList() ?? new List<object>();

                    branchesInfo.Add(new
                    {
                        branch = alias,
                        input = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(job.Input),
                        output = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(job.Output),
                        columns = columns
                    });
                }
                catch (Exception ex)
                {
                    branchesInfo.Add(new
                    {
                        branch = alias,
                        input = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(job.Input),
                        error = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message)
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Dry-run completed successfully.",
                branches = branchesInfo
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                stage = "dry-run",
                errors = new[] { DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message) }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }


    [McpServerTool(Name = "list-cursors")]
    [System.ComponentModel.Description("List active incremental cursors stored in the current workspace. Finds all state files and shows column, last value, last run time, status and row count.")]
    public string ListCursors()
    {
        var cursors = new List<object>();
        try
        {
            var workspaceDir = Directory.GetCurrentDirectory();
            var jsonFiles = Directory.EnumerateFiles(workspaceDir, "*.json", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/") && !f.Contains("/node_modules/") && !f.Contains("/.git/") && !f.Contains("/.agents/") && !f.Contains("/.gemini/"));

            foreach (var file in jsonFiles)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cursor", out var cursorProp) && root.TryGetProperty("version", out _))
                    {
                        var relativePath = Path.GetRelativePath(workspaceDir, file);
                        var col = cursorProp.TryGetProperty("column", out var colProp) ? colProp.GetString() : null;
                        var val = cursorProp.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;
                        var type = cursorProp.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                        string? status = null;
                        string? lastRun = null;
                        long rows = 0;

                        if (root.TryGetProperty("last_run", out var lastRunProp))
                        {
                            status = lastRunProp.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
                            lastRun = lastRunProp.TryGetProperty("completed_at", out var completedProp) ? completedProp.GetString() : null;
                            rows = lastRunProp.TryGetProperty("rows_transferred", out var rowsProp) && rowsProp.ValueKind == JsonValueKind.Number ? rowsProp.GetInt64() : 0;
                        }

                        cursors.Add(new
                        {
                            stateFile = relativePath,
                            column = col,
                            value = val,
                            type = type,
                            status = status,
                            lastRunCompletedAt = lastRun,
                            rowsTransferred = rows
                        });
                    }
                }
                catch
                {
                    // Ignore invalid JSON files
                }
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to list cursors: {ex.Message}" });
        }

        if (cursors.Count == 0)
        {
            return JsonSerializer.Serialize(new { info = "No active cursor state files found in the current workspace." });
        }

        return JsonSerializer.Serialize(cursors, new JsonSerializerOptions { WriteIndented = true });
    }


    [McpServerTool(Name = "suggest-pipeline")]
    [System.ComponentModel.Description("Given a source and destination, generate a ready-to-validate YAML pipeline configuration. Inspects the source schema and produces a complete YAML skeleton with correct column names and types.")]
    public async Task<string> SuggestPipeline(
        [System.ComponentModel.Description("Source connection string with provider prefix (e.g. 'csv:data.csv', 'pg:Host=...')")] string source,
        [System.ComponentModel.Description("Destination connection string with provider prefix (e.g. 'sqlite:output.db', 'parquet:output.parquet')")] string destination,
        [System.ComponentModel.Description("Optional: SQL query for database sources")] string? query = null,
        CancellationToken ct = default)
    {
        try
        {
            var registry = _serviceProvider.GetRequiredService<OptionsRegistry>();
            var readerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamReaderFactory>>().ToList();
            var writerFactories = _serviceProvider.GetRequiredService<IEnumerable<IDataWriterFactory>>().ToList();

            var resolvedReader = TryResolveReader(registry, readerFactories, source, query);
            if (resolvedReader == null)
            {
                return JsonSerializer.Serialize(new { error = $"Could not resolve reader provider for source '{source}'." });
            }

            List<PipeColumnInfo>? columns = null;
            string? schemaError = null;
            try
            {
                await using var reader = resolvedReader.Factory.Create(registry);
                await reader.OpenAsync(ct);
                columns = reader.Columns?.ToList();
            }
            catch (Exception ex)
            {
                schemaError = ex.Message;
            }

            var resolvedWriter = TryResolveWriter(writerFactories, destination);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("main:");
            sb.AppendLine($"  input: \"{source.Replace("\"", "\\\"")}\"");
            if (!string.IsNullOrEmpty(query))
            {
                sb.AppendLine($"  query: \"{query.Replace("\"", "\\\"")}\"");
            }
            sb.AppendLine($"  output: \"{destination.Replace("\"", "\\\"")}\"");

            if (schemaError != null)
            {
                sb.AppendLine();
                sb.AppendLine($"  # Warning: Could not inspect source schema: {schemaError}");
            }
            else if (columns != null && columns.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  # Source schema detected:");
                foreach (var col in columns)
                {
                    sb.AppendLine($"  #   - {col.Name} ({col.ClrType?.Name ?? "unknown"}, nullable: {col.IsNullable})");
                }
                sb.AppendLine();
                sb.AppendLine("  # Uncomment and modify to select or transform columns:");
                sb.AppendLine("  # transformers:");
                sb.AppendLine("  #   - type: project");
                sb.AppendLine("  #     columns:");
                foreach (var col in columns)
                {
                    sb.AppendLine($"  #       - {col.Name}");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to suggest pipeline: {ex.Message}" });
        }
    }


    private IDataWriterFactory? TryResolveWriter(
        List<IDataWriterFactory> writerFactories,
        string output)
    {
        IDataWriterFactory? factory = null;

        foreach (var f in writerFactories)
        {
            if (output.StartsWith(f.ComponentName + ":", StringComparison.OrdinalIgnoreCase))
            {
                factory = f;
                break;
            }
        }

        if (factory == null)
        {
            foreach (var f in writerFactories)
            {
                if (f.CanHandle(output))
                {
                    factory = f;
                    break;
                }
            }
        }

        return factory;
    }


    private static string? TryGetQueryFromJob(JobDefinition job, string componentName)
    {
        if (job.ProviderOptions == null) return null;

        var keys = new[] { componentName, componentName + "-reader", componentName + "-writer" };
        foreach (var key in keys)
        {
            if (job.ProviderOptions.TryGetValue(key, out var opts) && opts.TryGetValue("query", out var queryObj))
            {
                return queryObj?.ToString();
            }
        }

        return null;
    }

}
