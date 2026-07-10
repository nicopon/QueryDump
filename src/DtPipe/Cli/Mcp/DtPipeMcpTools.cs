using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;
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

    [McpServerTool(Name = "help")]
    [System.ComponentModel.Description("Show dtpipe CLI usage syntax, connection string rules, DAG capabilities, YAML job execution, and transformers options.")]
    public string Help()
    {
        using var sw = new StringWriter();
        sw.WriteLine("dtpipe — Data streaming & anonymization CLI");
        sw.WriteLine();
        sw.WriteLine("USAGE & PIPELINE ORDER:");
        sw.WriteLine("  A dtpipe command line must reflect the flow of data from left to right:");
        sw.WriteLine("  dtpipe -i <input> [reader-options] [transformers...] -o <output> [writer-options]");
        sw.WriteLine();
        sw.WriteLine("  1. INPUT: Define the source database/file (-i) and any reader-specific options first.");
        sw.WriteLine("  2. TRANSFORMATIONS: Transformers (like --fake, --mask, --filter, --compute) execute in the exact order they are specified on the command line from left to right.");
        sw.WriteLine("     Consecutive flags of the same type are grouped into one step. Specifying a different type starts a next step.");
        sw.WriteLine("     Example: '--fake A --fake B --filter C --fake D' executes: Fake(A, B) -> Filter(C) -> Fake(D).");
        sw.WriteLine("  3. OUTPUT OR ALIAS: Specify where the data goes (-o) or name the current branch output using '--alias <name>' to reference it downstream in a DAG.");
        sw.WriteLine();
        sw.WriteLine("  Alternative YAML job syntax: dtpipe --job <file.yaml> [overrides]");
        sw.WriteLine();
        sw.WriteLine("  QUOTING ARGUMENTS:");
        sw.WriteLine("    Any arguments containing spaces (such as connection strings with spaces, e.g., 'sqlite:Data Source=path/to/db', or compute/filter scripts with spaces) MUST be wrapped in double quotes (or single quotes) when invoking the command to prevent the parser from splitting the arguments.");
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
        sw.WriteLine("DAG TOPOLOGIES & ROUTING OPTIONS:");
        sw.WriteLine("  dtpipe can execute multi-branch pipelines forming a Directed Acyclic Graph (DAG).");
        sw.WriteLine("  - --alias <name>             Name the current branch for downstream references.");
        sw.WriteLine("  - --from <aliasA,aliasB>     Read from upstream branch aliases.");
        sw.WriteLine("  - --ref <alias>              Materialized secondary source for JOIN lookups (fully preloaded in memory).");
        sw.WriteLine("  - --sql <query>              Execute standard SQL using internal DuckDB engine.");
        sw.WriteLine("  - --merge                    UNION ALL of all --from streaming inputs.");
        sw.WriteLine("  Example (Join main & lookup):");
        sw.WriteLine("    dtpipe -i main.csv --alias m -i lookup.csv --alias l --from m --ref l --sql \"SELECT * FROM m JOIN l ON m.id = l.id\" -o target.csv");
        sw.WriteLine();
        sw.WriteLine("YAML JOBS:");
        sw.WriteLine("  - You can load/run pipelines from a YAML file using the '--job <file>' flag.");
        sw.WriteLine("  - You can export the pipeline described by a command line to a YAML file using the '--export-job <file>' option.");
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
        sw.WriteLine("  - --cursor <column>          Observer cursor column (e.g. updated_at) to track max value.");
        sw.WriteLine("  - --state <path>             State file path (JSON) to save the tracked cursor.");
        sw.WriteLine("  - --cursor-from <value>      Override start cursor value.");
        sw.WriteLine();
        sw.WriteLine("TRANSFORMERS OPTIONS:");
        var transformers = _transformerFactories.OfType<ICliContributor>().ToList();
        foreach (var contributor in transformers)
        {
            var flags = contributor.GetFlagDefs().ToList();
            if (flags.Count == 0) continue;
            
            if (contributor is IDataFactory factory)
            {
                sw.WriteLine($"  [{factory.ComponentName}]");
            }
            
            foreach (var flag in flags)
            {
                var aliases = flag.Aliases.Length > 0 ? $", {string.Join(", ", flag.Aliases)}" : "";
                var arity = flag.Arity switch
                {
                    FlagArity.Boolean    => "",
                    FlagArity.Scalar     => " <value>",
                    FlagArity.Repeatable => " <value...>",
                    _                    => ""
                };
                var desc = flag.Description ?? "";
                sw.WriteLine($"    {flag.Name}{aliases}{arity} : {desc}");
            }
        }
        
        return sw.ToString();
    }

    [McpServerTool(Name = "get-anonymization-help")]
    [System.ComponentModel.Description("Show detailed help on data faking (anonymization) via Bogus, including available datasets, methods, and options.")]
    public string GetAnonymizationHelp()
    {
        using var sw = new StringWriter();
        sw.WriteLine("ANONYMIZATION VIA FAKERS:");
        sw.WriteLine("  dtpipe uses the 'Bogus' library for generating fake data.");
        sw.WriteLine("  Syntax: --fake <column>:<dataset>.<method>");
        sw.WriteLine("  Example: --fake Email:internet.email --fake Name:name.fullName");
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
        sw.WriteLine("ANONYMIZATION OPTIONS:");
        sw.WriteLine("  - --fake-locale <locale>    Locale for fakers (e.g. 'fr', 'en', 'de', 'es').");
        sw.WriteLine("  - --fake-seed <int>         Global seed for reproducible faking.");
        sw.WriteLine("  - --fake-seed-column <cols> Column(s) used as a composite seed for faking (ensures same source cell always generates same fake value).");
        sw.WriteLine("  - --fake-seed-row           Row-index based deterministic faking (row N always gets same fakes).");
        sw.WriteLine("  - --skip-null               Do not anonymize NULL cell values (remains NULL).");
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

    [McpServerTool(Name = "validate-pipeline")]
    [System.ComponentModel.Description(
        "Validate a dtpipe command line. " +
        "Syntax: 'dtpipe -i <input> [transformers] -o <output>'. " +
        "Inputs/outputs format: 'provider:path' (e.g., 'csv:source.csv', 'parquet:target.parquet'). " +
        "Call the 'help' tool first to see all available transformers, options, and flags.")]
    public string ValidatePipeline(
        [System.ComponentModel.Description("The full dtpipe command line string to validate. IMPORTANT: Arguments containing spaces or special characters (such as JS compute expressions or strings with spaces) MUST be enclosed in double quotes, e.g.: --compute \"total:parseFloat(row.amount) * (1 + parseFloat(row.tax_rate))\" --filter \"row.total > 100\"")] string command)
    {
        try
        {
            // Clean up command prefix if present
            string cmdLine = command.Trim();
            if (cmdLine.StartsWith("dtpipe ", StringComparison.OrdinalIgnoreCase))
            {
                cmdLine = cmdLine.Substring(7).Trim();
            }

            var args = SplitArguments(cmdLine);

            var registry = FlagRegistryFactory.Build(_serviceProvider);
            var streamTransformerFactories = _serviceProvider.GetRequiredService<IEnumerable<IStreamTransformerFactory>>();

            var lexer = new PipelineLexer(registry);
            var parsedPipeline = lexer.Parse(args);
            
            var secretsManager = _serviceProvider.GetRequiredService<DtPipe.Cli.Security.ISecretsManager>();
            var (jobs, dag, _) = PipelineToJobConverter.Convert(parsedPipeline, streamTransformerFactories, secretsManager);

            var errors = PipelineValidator.Validate(dag, jobs, streamTransformerFactories);

            if (errors.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    errors
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            // Return success with DAG metadata
            var branches = dag.Branches.Select(b => new
            {
                b.Alias,
                StreamingFrom = b.StreamingAliases,
                Referencing = b.RefAliases,
                Input = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(b.Input),
                Output = DtPipe.Core.Security.ConnectionStringSanitizer.Sanitize(b.Output),
                Processor = b.ProcessorName ?? "none",
                TransformersCount = jobs.TryGetValue(b.Alias, out var j) ? j.Transformers?.Count ?? 0 : 0
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Pipeline syntax and topology are valid.",
                branches
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

    [McpServerTool(Name = "execute-pipeline")]
    [System.ComponentModel.Description(
        "Execute a dtpipe pipeline. This will READ from the source, apply transformations, and WRITE to the destination. " +
        "Syntax: 'dtpipe -i <input> [transformers] -o <output>'. " +
        "Inputs/outputs format: 'provider:path' (e.g., 'csv:source.csv', 'parquet:target.parquet'). " +
        "Call the 'help' tool first to see all available transformers, options, and flags.")]
    public async Task<string> ExecutePipeline(
        [System.ComponentModel.Description("The full dtpipe command line string to execute. IMPORTANT: Any arguments containing spaces (including connection strings with spaces, e.g. \"sqlite:Data Source=path/to/db\", or compute/filter scripts with spaces, e.g. --compute \"total:parseFloat(row.amount)\") MUST be wrapped in double quotes to prevent the command parser from splitting the arguments.")] string command,
        CancellationToken ct = default)
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
