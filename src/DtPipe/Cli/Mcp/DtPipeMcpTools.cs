using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;
using DtPipe.Core.Attributes;
using DtPipe.Core.Models;
using DtPipe.Core.Pipelines.Dag;
using DtPipe.Configuration;
using DtPipe.Cli.Pipeline;
using DtPipe.Cli.Infrastructure;
using ModelContextProtocol.Server;

namespace DtPipe.Cli.Mcp;

public class DtPipeMcpTools
{
    private readonly IEnumerable<IStreamReaderFactory> _readerFactories;
    private readonly IEnumerable<IDataTransformerFactory> _transformerFactories;
    private readonly IEnumerable<IDataWriterFactory> _writerFactories;
    private readonly IServiceProvider _serviceProvider;

    public DtPipeMcpTools(
        IEnumerable<IStreamReaderFactory> readerFactories,
        IEnumerable<IDataTransformerFactory> transformerFactories,
        IEnumerable<IDataWriterFactory> writerFactories,
        IServiceProvider serviceProvider)
    {
        _readerFactories = readerFactories;
        _transformerFactories = transformerFactories;
        _writerFactories = writerFactories;
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
        "Register a YAML job configuration in memory. Returns a virtual memory:// URI that can be used with '--job' option in execute-pipeline or validate-pipeline. " +
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
        using var sw = new StringWriter();
        sw.WriteLine("dtpipe — Data streaming & anonymization engine");
        sw.WriteLine();
        sw.WriteLine("YAML JOB USAGE (RECOMMENDED FOR AGENTS):");
        sw.WriteLine("  To run pipelines, execute a YAML job configuration using the 'execute-yaml-job' tool.");
        sw.WriteLine("  This is highly structured and completely avoids command-line quoting or shell escaping issues.");
        sw.WriteLine();
        sw.WriteLine("YAML JOB STRUCTURE:");
        sw.WriteLine("  A YAML job configuration is defined by named branches (typically 'main' for simple pipelines):");
        sw.WriteLine("  main:");
        sw.WriteLine("    input: \"<input-connection-string>\"");
        sw.WriteLine("    output: \"<output-connection-string>\"");
        sw.WriteLine("    provider-options:           # Optional: adapter-specific settings");
        sw.WriteLine("      <adapter-name>-reader:");
        sw.WriteLine("        <option-name>: <option-value>");
        sw.WriteLine("      <adapter-name>-writer:");
        sw.WriteLine("        <option-name>: <option-value>");
        sw.WriteLine("    transformers:               # Optional: list of transformers");
        sw.WriteLine("      - type: <transformer-name>");
        sw.WriteLine("        mappings:               # Column-level transformations");
        sw.WriteLine("          <column-name>: <expression_or_faker>");
        sw.WriteLine("        options:                # Transformer-level options");
        sw.WriteLine("          <option-name>: <option-value>");
        sw.WriteLine();
        sw.WriteLine("CONNECTION STRINGS:");
        sw.WriteLine("  - Files: use prefix:path (e.g., 'csv:file.csv', 'parquet:file.parquet', 'jsonl:file.jsonl').");
        sw.WriteLine("    If connection string is '-' or bare prefix (e.g. 'csv'), it reads from STDIN or writes to STDOUT.");
        sw.WriteLine("  - Databases: use ADO.NET connection string format (semicolon-separated pairs Key=Value;) instead of Python URIs.");
        sw.WriteLine("    - PostgreSQL: 'pg:Host=host;Port=port;Database=db;Username=user;Password=pass'");
        sw.WriteLine("    - SQLite: 'sqlite:Data Source=path/to/file.db'");
        sw.WriteLine("    - SQL Server: 'mssql:Server=host;Database=db;User Id=user;Password=pass;TrustServerCertificate=True'");
        sw.WriteLine("    - Oracle: 'ora:Data Source=host:port/service;User Id=user;Password=pass'");
        sw.WriteLine();
        sw.WriteLine("DAG TOPOLOGIES & ROUTING IN YAML:");
        sw.WriteLine("  dtpipe can execute multi-branch pipelines forming a Directed Acyclic Graph (DAG) by defining multiple named branches:");
        sw.WriteLine("  branch1:");
        sw.WriteLine("    input: \"csv:main.csv\"");
        sw.WriteLine("  branch2:");
        sw.WriteLine("    input: \"csv:lookup.csv\"");
        sw.WriteLine("  main:");
        sw.WriteLine("    from: \"branch1\"            # Streaming alias source");
        sw.WriteLine("    ref: [ \"branch2\" ]         # Preloaded lookup references");
        sw.WriteLine("    provider-options:");
        sw.WriteLine("      sql:");
        sw.WriteLine("        query: \"SELECT * FROM branch1 JOIN branch2 ON branch1.id = branch2.id\"");
        sw.WriteLine("    output: \"csv:target.csv\"");
        sw.WriteLine();
        sw.WriteLine("VALUE RESOLUTION & INLINE INTERPOLATION:");
        sw.WriteLine("  Values are resolved sequentially prior to execution:");
        sw.WriteLine("  - @/path/to/file             Loads full content from file.");
        sw.WriteLine("  - keyring://<alias>          Loads secret value from OS Keyring.");
        sw.WriteLine("  - ${{ENV_VAR}}               Substitutes environment variable.");
        sw.WriteLine("  - ${{keyring://<alias>}}     Substitutes inline keyring secret.");
        sw.WriteLine("  - ${{cursor://path|default}}  Substitutes incremental cursor value from a state file.");
        sw.WriteLine();
        sw.WriteLine("INCREMENTAL SYNC:");
        sw.WriteLine("  Define these keys directly in the branch root:");
        sw.WriteLine("  - cursor: <column_name>");
        sw.WriteLine("  - state: <path_to_state_file>");
        sw.WriteLine();

        var readerAdapters = _readerFactories.Select(f => f.ComponentName.ToLowerInvariant());
        var writerAdapters = _writerFactories.Select(f => f.ComponentName.ToLowerInvariant());
        var allAdapters = readerAdapters.Union(writerAdapters).OrderBy(x => x).ToList();
        var adaptersStr = string.Join(", ", allAdapters);
        sw.WriteLine("ADAPTERS:");
        sw.WriteLine($"  Available adapters: {adaptersStr}");
        sw.WriteLine("  To see YAML schema options and guidelines for a specific adapter, call the 'get-adapter-help' tool.");
        sw.WriteLine("  Example: get-adapter-help sqlite");
        sw.WriteLine();

        var allTransformers = _transformerFactories.Select(f => f.ComponentName.ToLowerInvariant()).OrderBy(x => x).ToList();
        var transformersStr = string.Join(", ", allTransformers);
        sw.WriteLine("TRANSFORMERS:");
        sw.WriteLine($"  Available transformers: {transformersStr}");
        sw.WriteLine("  To see YAML schema mappings, options, and examples for a specific transformer, call the 'get-transformer-help' tool.");
        sw.WriteLine("  Example: get-transformer-help compute");
        sw.WriteLine();

        return sw.ToString();
    }

    [McpServerTool(Name = "get-adapter-help")]
    [System.ComponentModel.Description("Show detailed help on a specific data adapter, including its usage as a reader or writer, and its specific options/flags in YAML.")]
    public string GetAdapterHelp(
        [System.ComponentModel.Description("Name of the adapter (e.g. 'csv', 'sqlite'). Call the 'list-providers' tool to discover all available reader/writer adapter names.")] string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return JsonSerializer.Serialize(new { error = "Adapter name cannot be empty." });

        var normalized = adapterName.Trim().ToLowerInvariant();
        var readers = _readerFactories.Where(f => f.ComponentName.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
        var writers = _writerFactories.Where(f => f.ComponentName.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToList();

        if (readers.Count == 0 && writers.Count == 0)
            return JsonSerializer.Serialize(new { error = $"Unknown adapter '{adapterName}'." });

        using var sw = new StringWriter();
        sw.WriteLine($"ADAPTER: {normalized}");
        sw.WriteLine(new string('=', normalized.Length + 9));
        sw.WriteLine();

        var optionType = readers.FirstOrDefault()?.OptionsType ?? writers.FirstOrDefault()?.OptionsType;
        if (optionType != null)
        {
            var descAttr = optionType.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            if (descAttr != null)
            {
                sw.WriteLine(descAttr.Description);
                sw.WriteLine();
            }
        }

        sw.WriteLine("YAML Provider Options Configuration:");
        sw.WriteLine($"  Place these under 'provider-options' -> '{normalized}' (or specific role suffix '{normalized}-reader' / '{normalized}-writer'):");

        if (readers.Count > 0)
        {
            sw.WriteLine("  Role: Reader (Data Source)");
            foreach (var r in readers)
            {
                var props = r.OptionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite)
                    .ToList();
                foreach (var prop in props)
                {
                    var kebabName = prop.Name.ToKebabCase();
                    var cliOptionAttr = prop.GetCustomAttribute<ComponentOptionAttribute>();
                    var descriptionAttr = prop.GetCustomAttribute<DescriptionAttribute>();
                    var desc = cliOptionAttr?.Description ?? descriptionAttr?.Description ?? string.Empty;

                    sw.WriteLine($"    {kebabName}: <value>");
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sw.WriteLine($"      # {desc}");
                    }
                }
            }
            sw.WriteLine();
        }

        if (writers.Count > 0)
        {
            sw.WriteLine("  Role: Writer (Data Destination)");
            foreach (var w in writers)
            {
                var props = w.OptionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite)
                    .ToList();
                foreach (var prop in props)
                {
                    var kebabName = prop.Name.ToKebabCase();
                    var cliOptionAttr = prop.GetCustomAttribute<ComponentOptionAttribute>();
                    var descriptionAttr = prop.GetCustomAttribute<DescriptionAttribute>();
                    var desc = cliOptionAttr?.Description ?? descriptionAttr?.Description ?? string.Empty;

                    sw.WriteLine($"    {kebabName}: <value>");
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sw.WriteLine($"      # {desc}");
                    }
                }
            }
            sw.WriteLine();
        }

        if (optionType != null)
        {
            var helpAttr = optionType.GetCustomAttribute<DtPipe.Core.Attributes.ComponentHelpAttribute>();
            if (helpAttr != null)
            {
                if (!string.IsNullOrEmpty(helpAttr.UsageNotes))
                {
                    sw.WriteLine("YAML Usage & Notes:");
                    sw.WriteLine($"  {helpAttr.UsageNotes}");
                    sw.WriteLine();
                }

                if (helpAttr.Examples != null && helpAttr.Examples.Length > 0)
                {
                    sw.WriteLine("YAML Example Configuration:");
                    foreach (var ex in helpAttr.Examples)
                    {
                        sw.WriteLine(ex);
                    }
                    sw.WriteLine();
                }
            }
        }

        return sw.ToString();
    }

    [McpServerTool(Name = "get-transformer-help")]
    [System.ComponentModel.Description("Show detailed help on a specific transformer, including its YAML options and examples.")]
    public string GetTransformerHelp(
        [System.ComponentModel.Description("Name of the transformer (e.g. 'compute', 'fake'). Call the 'list-providers' tool to discover all available transformer names.")] string transformerName)
    {
        if (string.IsNullOrWhiteSpace(transformerName))
            return JsonSerializer.Serialize(new { error = "Transformer name cannot be empty." });

        var normalized = transformerName.Trim().ToLowerInvariant();

        var factory = _transformerFactories.FirstOrDefault(f => f.ComponentName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (factory == null)
            return JsonSerializer.Serialize(new { error = $"Unknown transformer '{transformerName}'." });

        using var sw = new StringWriter();
        sw.WriteLine($"TRANSFORMER: {normalized}");
        sw.WriteLine(new string('=', normalized.Length + 13));
        sw.WriteLine();

        var descAttr = factory.OptionsType.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        if (descAttr != null)
        {
            sw.WriteLine(descAttr.Description);
            sw.WriteLine();
        }

        var properties = factory.OptionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToList();

        if (properties.Count > 0)
        {
            sw.WriteLine("YAML Options Configuration:");
            sw.WriteLine("  Place these options under the 'options' block of the transformer:");
            foreach (var prop in properties)
            {
                var kebabName = prop.Name.ToKebabCase();
                if (kebabName == normalized || kebabName == "filters" || kebabName == "mask" || kebabName == "fake")
                    continue;

                var cliOptionAttr = prop.GetCustomAttribute<ComponentOptionAttribute>();
                var descriptionAttr = prop.GetCustomAttribute<DescriptionAttribute>();
                var desc = cliOptionAttr?.Description ?? descriptionAttr?.Description ?? string.Empty;

                sw.WriteLine($"  {kebabName}: <value>");
                if (!string.IsNullOrEmpty(desc))
                {
                    sw.WriteLine($"    # {desc}");
                }
            }
            sw.WriteLine();
        }

        var helpAttr = factory.OptionsType.GetCustomAttribute<DtPipe.Core.Attributes.ComponentHelpAttribute>();
        if (helpAttr != null)
        {
            if (!string.IsNullOrEmpty(helpAttr.UsageNotes))
            {
                sw.WriteLine("YAML Usage & Notes:");
                sw.WriteLine($"  {helpAttr.UsageNotes}");
                sw.WriteLine();
            }

            if (helpAttr.Examples != null && helpAttr.Examples.Length > 0)
            {
                sw.WriteLine("YAML Example Configuration:");
                foreach (var ex in helpAttr.Examples)
                {
                    sw.WriteLine(ex);
                }
                sw.WriteLine();
            }
        }

        if (normalized == "fake")
        {
            sw.WriteLine(GetAnonymizationHelp());
        }

        return sw.ToString();
    }

    [McpServerTool(Name = "get-anonymization-help")]
    [System.ComponentModel.Description("Show detailed help on data faking (anonymization) via Bogus, including available datasets, methods, and options for the YAML job schema.")]
    public string GetAnonymizationHelp()
    {
        using var sw = new StringWriter();
        sw.WriteLine("ANONYMIZATION VIA FAKERS (YAML SCHEMA):");
        sw.WriteLine("  dtpipe uses the 'Bogus' library for generating fake data.");
        sw.WriteLine("  Specify column-level fakers in the 'mappings' section of the 'fake' transformer.");
        sw.WriteLine("  Syntax (under mappings):");
        sw.WriteLine("    <column_name>: <dataset>.<method>");
        sw.WriteLine("  Example YAML:");
        sw.WriteLine("    transformers:");
        sw.WriteLine("      - type: fake");
        sw.WriteLine("        mappings:");
        sw.WriteLine("          Email: internet.email");
        sw.WriteLine("          Name: name.fullName");
        sw.WriteLine();
        sw.WriteLine("AVAILABLE DATASETS & METHODS (DYNAMICALLY RESOLVED):");

        var fakerRegistry = new DtPipe.Transformers.Arrow.Fake.FakerRegistry();
        foreach (var group in fakerRegistry.ListAll())
        {
            sw.WriteLine($"  - {group.Dataset.ToLowerInvariant()}");
            foreach (var method in group.Methods)
            {
                var paddedMethod = method.Method.PadRight(25);
                var desc = string.IsNullOrEmpty(method.Description) ? "" : $" {method.Description}";
                sw.WriteLine($"    - {paddedMethod}{desc}");
            }
        }

        sw.WriteLine();
        sw.WriteLine("ANONYMIZATION OPTIONS (place these under 'options' block):");
        sw.WriteLine("  - fake-locale: <string>     Locale for fakers (e.g. 'fr', 'en', 'de', 'es').");
        sw.WriteLine("  - fake-seed: <int>          Global seed for reproducible faking.");
        sw.WriteLine("  - fake-seed-column: [cols]  Column(s) used as a composite seed (YAML array).");
        sw.WriteLine("  - fake-seed-row: <bool>     Row-index based deterministic faking.");
        sw.WriteLine("  - skip-null: <bool>         Do not anonymize NULL cell values (remains NULL).");
        return sw.ToString();
    }

    [McpServerTool(Name = "inspect")]
    [System.ComponentModel.Description("Inspect the schema of a data source. Example input: 'pg:Host=localhost;Database=prod;Username=postgres' or 'csv:file.csv'")]
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

            if (result.Factory.RequiresQuery && string.IsNullOrWhiteSpace(query))
                return JsonSerializer.Serialize(new { error = $"A query is required for provider '{result.Factory.ComponentName}'." });

            await using var reader = result.Factory.Create(registry);
            await reader.OpenAsync(ct);

            if (reader.Columns == null || reader.Columns.Count == 0)
                return JsonSerializer.Serialize(new { warning = "No columns returned." });

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
            var errors = PipelineValidator.Validate(dag, jobs, streamTransformerFactories);

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

        var name = "job-" + Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(Path.GetTempPath(), "dtpipe-job-" + name + ".yaml");

        try
        {
            File.WriteAllText(tempPath, yamlContent);
            return await ExecutePipelineInternal($"dtpipe --job memory://{name}", ct);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private async Task<string> ExecutePipelineInternal(string command, CancellationToken ct = default)
    {
        try
        {
            string cmdLine = command.Trim();
            if (cmdLine.StartsWith("dtpipe ", StringComparison.OrdinalIgnoreCase))
                cmdLine = cmdLine.Substring(7).Trim();

            var args = SplitArguments(cmdLine);

            var registry = FlagRegistryFactory.Build(_serviceProvider);
            var streamTransformerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamTransformerFactory>>();
            var lexer = new PipelineLexer(registry);
            var parsedPipeline = lexer.Parse(args);

            var secretsManager = _serviceProvider.GetRequiredService<DtPipe.Cli.Security.ISecretsManager>();
            var (jobs, dag, contexts) = PipelineToJobConverter.Convert(parsedPipeline, streamTransformerFactories, secretsManager);

            var errors = PipelineValidator.Validate(dag, jobs, streamTransformerFactories);
            if (errors.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    stage = "validation",
                    errors
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var jobService = _serviceProvider.GetRequiredService<DtPipe.Cli.JobService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var exitCode = await jobService.ExecutePipelineAsync(jobs, dag, contexts, parsedPipeline.Globals, ct);

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

    private static void ValidatePathSafety(string path)
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


    private static string[] SplitArguments(string commandLine)
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
}
