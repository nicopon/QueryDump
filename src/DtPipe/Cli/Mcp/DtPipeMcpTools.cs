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

    [McpServerTool(Name = "register-yaml-job")]
    [System.ComponentModel.Description(
        "Optional: Register a YAML job configuration in memory to receive a virtual memory:// URI. Note: To run YAML jobs, 'execute-yaml-job' accepts your YAML content string directly without needing to call 'register-yaml-job' first. " +
        "To discover valid adapter connection string prefixes (e.g. 'csv', 'sqlite') and transformer types (e.g. 'fake', 'compute'), call the 'list-providers' tool. " +
        "YAML Job Schema Example:\n" +
        "main:\n" +
        "  input: \"<adapter>:<source_path>\"\n" +
        "  transformers:\n" +
        "    - type: <transformer_type>\n" +
        "      mappings:\n" +
        "        <target_column>: <expression_or_format>\n" +
        "  output: \"<adapter>:<destination_path>\"")]
    public string RegisterYamlJob(
        [System.ComponentModel.Description("Unique name for the job (alphanumeric and hyphens only, e.g. 'my-sales-analysis')")] string name,
        [System.ComponentModel.Description("The complete YAML job configuration string")] string yamlContent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return JsonSerializer.Serialize(new { success = false, error = "Job name cannot be empty." });

            if (string.IsNullOrWhiteSpace(yamlContent))
                return JsonSerializer.Serialize(new { success = false, error = "YAML job content cannot be empty." });

            if (!Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$"))
                return JsonSerializer.Serialize(new { success = false, error = "Job name must contain only alphanumeric characters, underscores, or hyphens." });

            var tempPath = Path.Combine(Path.GetTempPath(), "dtpipe-job-" + name + ".yaml");
            File.WriteAllText(tempPath, yamlContent);

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "YAML job registered successfully in memory.",
                uri = $"memory://{name}"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
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
                var discoveryQuery = TryBuildTableDiscoveryQuery(result.Factory.ComponentName);
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

    [McpServerTool(Name = "validate-yaml-job")]
    [System.ComponentModel.Description("Validate a pipeline configuration specified directly as YAML. Checks for syntax errors and schema validation issues without executing.")]
    public string ValidateYamlJob(
        [System.ComponentModel.Description("The complete YAML configuration string representing the pipeline")] string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return JsonSerializer.Serialize(new { success = false, error = "YAML job content cannot be empty." });

        try
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

            if (errors.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    errors
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

            if (errors.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    stage = "validation",
                    errors
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var contexts = jobs.ToDictionary(
                kv => kv.Key,
                kv => new CliJobContext(null, null, null, Array.Empty<string>())
            );

            var jobService = _serviceProvider.GetRequiredService<DtPipe.Cli.JobService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var globalOptions = new GlobalOptions();
            var exitCode = await jobService.ExecutePipelineAsync(jobs, dag, contexts, globalOptions, ct);
            sw.Stop();

            return JsonSerializer.Serialize(new
            {
                success = exitCode == 0,
                exitCode,
                durationMs = sw.ElapsedMilliseconds,
                branches = dag.Branches.Select(b => new
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
                var optionsType = f.GetSupportedOptionTypes().FirstOrDefault();
                if (optionsType != null)
                {
                    var instance = registry.Get(optionsType);
                    optionsType.GetProperty("Input")?.SetValue(instance, effectiveConnectionString);
                    registry.RegisterByType(optionsType, instance);
                }
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
                    var optionsType = f.GetSupportedOptionTypes().FirstOrDefault();
                    if (optionsType != null)
                    {
                        var instance = registry.Get(optionsType);
                        optionsType.GetProperty("Input")?.SetValue(instance, input);
                        registry.RegisterByType(optionsType, instance);
                    }
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

        if (!string.IsNullOrEmpty(query))
        {
            var optionsType = factory.GetSupportedOptionTypes().FirstOrDefault();
            if (optionsType != null)
            {
                var instance = registry.Get(optionsType);
                optionsType.GetProperty("Query")?.SetValue(instance, query);
                registry.RegisterByType(optionsType, instance);
            }
        }

        return new ReaderResolutionResult(factory, effectiveConnectionString);
    }

    private static string? TryBuildTableDiscoveryQuery(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            "sqlite" => "SELECT name AS table_name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name",
            "pg" or "postgresql" => "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_name",
            "mssql" or "sqlserver" => "SELECT TABLE_NAME AS table_name, TABLE_TYPE AS table_type FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME",
            "ora" or "oracle" => "SELECT table_name, 'TABLE' AS table_type FROM user_tables ORDER BY table_name",
            "duck" or "duckdb" => "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_name",
            _ => null
        };
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
