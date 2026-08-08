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
using ModelContextProtocol.Server;

namespace DtPipe.Cli.Mcp;

public class DtPipeMcpTools
{
    private readonly IEnumerable<IStreamReaderFactory> _readerFactories;
    private readonly IEnumerable<IDataTransformerFactory> _transformerFactories;
    private readonly IEnumerable<IDataWriterFactory> _writerFactories;
    private readonly IMcpHelpService _mcpHelpService;
    private readonly IServiceProvider _serviceProvider;

    public DtPipeMcpTools(
        IEnumerable<IStreamReaderFactory> readerFactories,
        IEnumerable<IDataTransformerFactory> transformerFactories,
        IEnumerable<IDataWriterFactory> writerFactories,
        IMcpHelpService mcpHelpService,
        IServiceProvider serviceProvider)
    {
        _readerFactories = readerFactories;
        _transformerFactories = transformerFactories;
        _writerFactories = writerFactories;
        _mcpHelpService = mcpHelpService;
        _serviceProvider = serviceProvider;
    }

    [McpServerTool(Name = "list-providers")]
    [System.ComponentModel.Description("List available data source providers, writers, and transformers in dtpipe")]
    public string ListProviders()
    {
        var readers = _readerFactories.Select(f => f.ComponentName).ToList();
        var transformers = _transformerFactories.Select(f => f.ComponentName).ToList();
        var writers = _writerFactories.Select(f => f.ComponentName).ToList();
        
        return JsonSerializer.Serialize(new 
        { 
            readers, 
            transformers, 
            writers 
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "help")]
    [System.ComponentModel.Description("Show general usage guidelines, YAML job structures, connection string rules, DAG capabilities, and list available adapters and transformers.")]
    public string Help()
    {
        return _mcpHelpService.GetGeneralHelp();
    }

    [McpServerTool(Name = "get-adapter-help")]
    [System.ComponentModel.Description("Show detailed help on a specific data adapter, including its usage as a reader or writer, and its specific options/flags in YAML.")]
    public string GetAdapterHelp(
        [System.ComponentModel.Description("Name of the adapter (e.g. 'csv', 'sqlite'). Call the 'list-providers' tool to discover all available reader/writer adapter names.")] string adapterName)
    {
        return _mcpHelpService.GetAdapterHelp(adapterName);
    }

    [McpServerTool(Name = "get-transformer-help")]
    [System.ComponentModel.Description("Show detailed help on a specific transformer, including its YAML options and examples.")]
    public string GetTransformerHelp(
        [System.ComponentModel.Description("Name of the transformer (e.g. 'compute', 'fake'). Call the 'list-providers' tool to discover all available transformer names.")] string transformerName)
    {
        return _mcpHelpService.GetTransformerHelp(transformerName);
    }

    [McpServerTool(Name = "get-anonymization-help")]
    [System.ComponentModel.Description("Show detailed help on data faking (anonymization) via Bogus, including available datasets, methods, and options for the YAML job schema.")]
    public string GetAnonymizationHelp()
    {
        return _mcpHelpService.GetAnonymizationHelp();
    }

    [McpServerTool(Name = "inspect")]
    [System.ComponentModel.Description("Inspect the schema of a data source. For database sources (e.g. 'sqlite:file.db', 'pg:Host=...'), if no query/table is passed, it automatically discovers and lists available tables and views in the database.")]
    public async Task<string> Inspect(
        [System.ComponentModel.Description("Connection string or file path with provider prefix")] string input, 
        [System.ComponentModel.Description("Optional SQL query for database sources")] string? query = null,
        CancellationToken ct = default)
    {
        try
        {
            var registry = _serviceProvider.GetRequiredService<OptionsRegistry>();
            var readerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamReaderFactory>>().ToList();

            var result = TryResolveReader(registry, readerFactories, input, query);
            if (result == null)
                return JsonSerializer.Serialize(new { error = "No provider found for the given input." });

            bool isTableDiscovery = false;
            if (result.Factory.RequiresQuery && string.IsNullOrWhiteSpace(query))
            {
                var discoveryQuery = TryBuildTableDiscoveryQuery(result.Factory);
                if (discoveryQuery != null)
                {
                    query = discoveryQuery;
                    result = TryResolveReader(registry, readerFactories, input, query);
                    isTableDiscovery = true;
                }
                else
                {
                    return JsonSerializer.Serialize(new { error = $"A query is required for provider '{result.Factory.ComponentName}'." });
                }
            }

            if (result == null)
                return JsonSerializer.Serialize(new { error = "Could not resolve provider with table discovery query." });

            await using var reader = result.Factory.Create(registry);
            await reader.OpenAsync(ct);

            if (reader.Columns == null || reader.Columns.Count == 0)
                return JsonSerializer.Serialize(new { warning = "No columns returned." });

            if (isTableDiscovery)
            {
                var tables = new List<Dictionary<string, object?>>();
                var cols = reader.Columns;
                await foreach (var batch in reader.ReadBatchesAsync(100, ct))
                {
                    var span = batch.Span;
                    for (int i = 0; i < span.Length; i++)
                    {
                        var row = span[i];
                        if (row == null) continue;
                        var dict = new Dictionary<string, object?>();
                        for (int c = 0; c < cols.Count && c < row.Length; c++)
                        {
                            dict[cols[c].Name] = row[c];
                        }
                        tables.Add(dict);
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    info = $"No query or table specified. Automatically discovered available tables/views in database '{result.Factory.ComponentName}':",
                    tables
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return JsonSerializer.Serialize(
                reader.Columns.Select(c => new {
                    c.Name, Type = c.ClrType?.Name ?? "unknown",
                    c.IsNullable
                }),
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message) });
        }
    }

    private record YamlParseResult(
        Dictionary<string, JobDefinition> Jobs,
        JobDagDefinition Dag,
        List<string> Errors);

    private YamlParseResult ParseAndValidateYaml(string yamlContent)
    {
        var secretsManager = _serviceProvider.GetService<DtPipe.Cli.Security.ISecretsManager>();
        var jobs = JobFileParser.ParseContent(yamlContent, secretsManager);

        var streamTransformerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamTransformerFactory>>();
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
                ?.ComponentName
        }).ToList();

        var dag = new JobDagDefinition { Branches = branches };
        var errors = PipelineValidator.Validate(dag, jobs, streamTransformerFactories).ToList();
        errors.AddRange(ValidateJobTransformers(jobs));

        return new YamlParseResult(jobs, dag, errors);
    }

    [McpServerTool(Name = "validate-yaml-job")]
    [System.ComponentModel.Description("Validate a pipeline configuration specified directly as YAML. Checks for syntax errors and schema validation issues without executing.")]
    public string ValidateYamlJob(
        [System.ComponentModel.Description("The complete YAML configuration string representing the pipeline")] string yamlContent)
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
                    errors = parsed.Errors
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "YAML job configuration and topology are valid."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                errors = new[] { DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message) }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [McpServerTool(Name = "execute-yaml-job")]
    [System.ComponentModel.Description("Execute a pipeline configuration specified directly as YAML. This is the only way to run pipelines and avoids command-line quoting/escaping issues.")]
    public async Task<string> ExecuteYamlJob(
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

            var contexts = parsed.Jobs.ToDictionary(
                kv => kv.Key,
                kv => new CliJobContext(null, null, null, Array.Empty<string>())
            );

            var jobService = _serviceProvider.GetRequiredService<DtPipe.Cli.JobService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var globalOptions = new GlobalOptions();
            var exitCode = await jobService.ExecutePipelineAsync(parsed.Jobs, parsed.Dag, contexts, globalOptions, ct);
            sw.Stop();

            return JsonSerializer.Serialize(new
            {
                success = exitCode == 0,
                exitCode,
                durationMs = sw.ElapsedMilliseconds,
                branches = parsed.Dag.Branches.Select(b => new
                {
                    b.Alias,
                    Input = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(b.Input),
                    Output = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(b.Output)
                }).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                stage = "execution",
                errors = new[] { DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message) }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [McpServerTool(Name = "preview-data")]
    [System.ComponentModel.Description("Preview data from a source (up to 10 rows, with automatic masking of sensitive columns). Example input: 'csv:file.csv' or 'pg:Host=localhost;Database=prod;Username=postgres'")]
    public async Task<string> PreviewData(
        [System.ComponentModel.Description("Connection string or file path with provider prefix")] string input,
        [System.ComponentModel.Description("Optional SQL query for database sources")] string? query = null,
        [System.ComponentModel.Description("Number of rows to return (max 10)")] int? limit = 5,
        CancellationToken ct = default)
    {
        try
        {
            var registry = _serviceProvider.GetRequiredService<OptionsRegistry>();
            var readerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamReaderFactory>>().ToList();

            var result = TryResolveReader(registry, readerFactories, input, query);
            if (result == null)
                return JsonSerializer.Serialize(new { error = "No provider found for the given input." });

            if (result.Factory.RequiresQuery && string.IsNullOrWhiteSpace(query))
                return JsonSerializer.Serialize(new { error = $"A query is required for provider '{result.Factory.ComponentName}'." });

            await using var reader = result.Factory.Create(registry);
            await reader.OpenAsync(ct);

            if (reader.Columns == null || reader.Columns.Count == 0)
                return JsonSerializer.Serialize(new { warning = "No columns returned." });

            int effectiveLimit = Math.Min(limit ?? 5, 10);
            var rowsList = new List<Dictionary<string, object?>>();
            var columns = reader.Columns;

            await foreach (var batch in reader.ReadBatchesAsync(effectiveLimit, ct))
            {
                var span = batch.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    var row = span[i];
                    if (row == null) continue;

                    var rowDict = new Dictionary<string, object?>();
                    for (int c = 0; c < columns.Count && c < row.Length; c++)
                    {
                        var colName = columns[c].Name;
                        rowDict[colName] = row[c];
                    }
                    rowsList.Add(rowDict);

                    if (rowsList.Count >= effectiveLimit)
                        break;
                }

                if (rowsList.Count >= effectiveLimit)
                    break;
            }

            return JsonSerializer.Serialize(rowsList, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(ex.Message) });
        }
    }

    internal static void ValidatePathSafety(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (path.StartsWith("duck+", StringComparison.OrdinalIgnoreCase)) return;

        // Clean query/parameters from SQLite/DuckDB connection strings
        string cleanPath = path;
        int semicolonIndex = cleanPath.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            // If it's a connection string containing key=value pairs, skip file path check
            if (cleanPath.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
                cleanPath.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                cleanPath.Contains("User Id=", StringComparison.OrdinalIgnoreCase) ||
                cleanPath.Contains("Database=", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            // Otherwise, it might be an SQLite/DuckDB file connection string like "Data Source=filename;..."
            // Extract the path if it contains "Data Source="
            var match = Regex.Match(cleanPath, @"Data\s+Source\s*=\s*(?<file>[^;]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                cleanPath = match.Groups["file"].Value.Trim();
            }
            else
            {
                cleanPath = cleanPath.Substring(0, semicolonIndex).Trim();
            }
        }

        // Strip quotes if any
        cleanPath = cleanPath.Trim('"', '\'');

        // Skip check if it is clearly in-memory
        if (string.Equals(cleanPath, ":memory:", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanPath, "-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(cleanPath);
            string currentDir = Path.GetFullPath(Directory.GetCurrentDirectory());

            if (!fullPath.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Access to path '{path}' is denied. Only files within the current workspace are accessible.");
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception)
        {
            // If Path.GetFullPath throws, it might be a database connection string with invalid characters.
            // We ignore it here and let the provider handle/reject it.
        }
    }

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

    private static string? TryBuildTableDiscoveryQuery(IStreamReaderFactory factory)
    {
        if (factory is IHasSqlDialect hasDialect && hasDialect.Dialect?.TableDiscoveryQuery != null)
        {
            return hasDialect.Dialect.TableDiscoveryQuery;
        }
        return null;
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

    private List<string> ValidateJobTransformers(Dictionary<string, JobDefinition> jobs)
    {
        var errors = new List<string>();
        var transformerFactories = _serviceProvider.GetRequiredService<IEnumerable<IDataTransformerFactory>>();
        var registeredTransformers = new HashSet<string>(transformerFactories.Select(f => f.ComponentName), StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, job) in jobs)
        {
            if (job.Transformers != null)
            {
                foreach (var t in job.Transformers)
                {
                    if (!string.IsNullOrEmpty(t.Type) && !registeredTransformers.Contains(t.Type))
                    {
                        errors.Add($"Branch '{alias}': Unknown transformer type '{t.Type}'. Call 'list-providers' to see valid transformer types (e.g., 'fake', 'compute', 'filter', 'project'). Note: Joins across data sources are configured using the 'sql' provider under 'provider-options', not as a transformer type.");
                    }
                }
            }
        }

        return errors;
    }
}
