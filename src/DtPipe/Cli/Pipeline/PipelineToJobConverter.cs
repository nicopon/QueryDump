using System;
using System.Collections.Generic;
using System.Linq;
using DtPipe.Configuration;
using DtPipe.Cli.Infrastructure;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Pipelines;
using DtPipe.Core.Pipelines.Dag;

namespace DtPipe.Cli.Pipeline;

public static class PipelineToJobConverter
{
    public static (Dictionary<string, JobDefinition> Jobs, JobDagDefinition Dag, Dictionary<string, CliJobContext> Contexts) Convert(
        ParsedPipeline parsed,
        IEnumerable<IStreamTransformerFactory>? streamTransformerFactories = null,
        DtPipe.Cli.Security.ISecretsManager? secretsManager = null,
        IEnumerable<IStreamReaderFactory>? readerFactories = null,
        IEnumerable<IDataWriterFactory>? writerFactories = null,
        IEnumerable<IDataTransformerFactory>? dataTransformerFactories = null)
    {
        // --job mode: load from YAML file and apply CLI overrides
        if (!string.IsNullOrEmpty(parsed.Globals.JobFile))
            return ConvertFromJobFile(parsed, streamTransformerFactories, secretsManager);

        var jobs = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        var contexts = new Dictionary<string, CliJobContext>(StringComparer.OrdinalIgnoreCase);
        var branches = new List<BranchDefinition>();

        // Pass 1: Collect explicit aliases to avoid collisions
        var explicitAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in parsed.Branches)
        {
            if (!string.IsNullOrEmpty(b.Alias))
                explicitAliases.Add(b.Alias);
        }

        var processorFactories = streamTransformerFactories?.ToList();

        int branchCounter = 1;
        foreach (var branchSpec in parsed.Branches)
        {
            var alias = branchSpec.Alias;
            if (string.IsNullOrEmpty(alias))
            {
                if (parsed.Branches.Count == 1)
                {
                    alias = "main";
                }
                else
                {
                    while (explicitAliases.Contains($"stream{branchCounter}"))
                        branchCounter++;
                    alias = $"stream{branchCounter}";
                    branchCounter++;
                }
            }

            var job = MapToJobDefinition(parsed.Globals, branchSpec);

            // F3 round-trip fidelity: reconstruct transformers and provider options so that
            // CLI → YAML (--export-job) → run (--job) preserves the full pipeline semantics.
            var processor = processorFactories?.FirstOrDefault(f => f.IsApplicable(branchSpec.RawArgs));
            job = job with
            {
                Transformers = BuildTransformerConfigs(branchSpec.PipelineArgs, dataTransformerFactories),
                ProviderOptions = BuildProviderOptions(
                    job.Input, job.Output,
                    branchSpec.ReaderArgs, branchSpec.WriterArgs,
                    readerFactories, writerFactories,
                    processor, branchSpec.RawArgs)
            };

            jobs[alias] = job;
            contexts[alias] = new CliJobContext(branchSpec.ReaderArgs, branchSpec.PipelineArgs, branchSpec.WriterArgs, branchSpec.RawArgs);
            branches.Add(new BranchDefinition
            {
                Alias = alias,
                Input = job.Input,
                Output = job.Output,
                StreamingAliases = branchSpec.From.ToArray(),
                RefAliases = branchSpec.Ref.ToArray(),
                Arguments = branchSpec.RawArgs,
                ProcessorName = processor?.ComponentName
            });
        }

        var dag = new JobDagDefinition { Branches = branches };
        return (jobs, dag, contexts);
    }

    private static (Dictionary<string, JobDefinition> Jobs, JobDagDefinition Dag, Dictionary<string, CliJobContext> Contexts) ConvertFromJobFile(
        ParsedPipeline parsed,
        IEnumerable<IStreamTransformerFactory>? streamTransformerFactories,
        DtPipe.Cli.Security.ISecretsManager? secretsManager)
    {
        var jobs = JobFileParser.Parse(parsed.Globals.JobFile!, secretsManager);
        var flags = parsed.Globals.AllFlags;

        // Apply CLI overrides to all loaded jobs
        int? limitOverride = GetInt(flags, "--limit");
        int? batchOverride = GetInt(flags, "--batch-size", "-b");
        string? logOverride = GetString(flags, "--log");
        string? metricsOverride = GetString(flags, "--metrics-path");

        foreach (var alias in jobs.Keys.ToList())
        {
            var job = jobs[alias];
            if (parsed.Globals.DryRunCount > 0) job = job with { DryRunCount = parsed.Globals.DryRunCount };
            if (limitOverride is > 0)           job = job with { Limit = limitOverride.Value };
            if (batchOverride is > 0)           job = job with { BatchSize = batchOverride.Value };
            if (!string.IsNullOrEmpty(logOverride))     job = job with { LogPath = logOverride };
            if (!string.IsNullOrEmpty(metricsOverride)) job = job with { MetricsPath = metricsOverride };
            jobs[alias] = job;
        }

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
            ProcessorName = streamTransformerFactories?
                .FirstOrDefault(f => f.IsApplicable(kv.Value))
                ?.ComponentName
        }).ToList();

        return (jobs, new JobDagDefinition { Branches = branches }, new Dictionary<string, CliJobContext>(StringComparer.OrdinalIgnoreCase));
    }

    private static JobDefinition MapToJobDefinition(GlobalOptions globals, BranchSpec branch)
    {
        // Engine-control values are extracted from AllFlags (global) and Flags (branch-local).
        // Branch-local values take precedence over global defaults.
        int batchSize = GetInt(branch.Flags, "--batch-size", "-b")
                     ?? GetInt(globals.AllFlags, "--batch-size", "-b")
                     ?? PipelineOptions.DefaultBatchSize;
        int limit = GetInt(branch.Flags, "--limit")
                 ?? GetInt(globals.AllFlags, "--limit")
                 ?? 0;
        double samplingRate = GetDouble(branch.Flags, "--sampling-rate", "--sample-rate")
                           ?? GetDouble(globals.AllFlags, "--sampling-rate", "--sample-rate")
                           ?? 1.0;
        int? samplingSeed = GetNullableInt(branch.Flags, "--sampling-seed", "--sample-seed")
                        ?? GetNullableInt(globals.AllFlags, "--sampling-seed", "--sample-seed");
        string? logPath = GetString(branch.Flags, "--log") ?? globals.LogPath;
        string? metricsPath = GetString(branch.Flags, "--metrics-path")
                           ?? GetString(globals.AllFlags, "--metrics-path");
        string? prefix = GetString(branch.Flags, "--prefix", "-p")
                      ?? GetString(globals.AllFlags, "--prefix", "-p");
        string? cursor = GetString(branch.Flags, "--cursor")
                      ?? GetString(globals.AllFlags, "--cursor");
        string? state = GetString(branch.Flags, "--state")
                     ?? GetString(globals.AllFlags, "--state");

        return new JobDefinition
        {
            Input  = branch.Input,
            Output = branch.Output,
            BatchSize    = batchSize,
            DryRunCount  = globals.DryRunCount,
            Limit        = limit,
            SamplingRate = samplingRate,
            SamplingSeed = samplingSeed,
            LogPath      = logPath,
            MetricsPath  = metricsPath,
            Prefix       = prefix,
            Cursor       = cursor,
            State        = state,
            NoStats      = globals.NoStats,

            // YAML convention: 'from' carries the comma-joined streaming aliases
            // (ConvertFromJobFile splits it back into StreamingAliases).
            From     = string.Join(",", branch.From),
            Ref      = branch.Ref.ToArray(),

            Transformers    = null,
            ProviderOptions = null
        };
    }

    // ── F3 round-trip reconstruction helpers ────────────────────────────────

    /// <summary>
    /// Rebuilds the YAML transformer configs from a branch's pipeline args using the same
    /// grouping rule as live execution (consecutive flags of one factory = one step).
    /// Returns null when no transformer args are present or no factories were supplied.
    /// </summary>
    private static List<TransformerConfig>? BuildTransformerConfigs(
        string[] pipelineArgs,
        IEnumerable<IDataTransformerFactory>? dataTransformerFactories)
    {
        if (dataTransformerFactories == null || pipelineArgs is not { Length: > 0 })
            return null;

        var builder = new DtPipe.Cli.Infrastructure.TransformerPipelineBuilder(dataTransformerFactories);
        var configs = new List<TransformerConfig>();
        foreach (var (factory, pairs) in builder.CollectGroups(pipelineArgs))
        {
            var instance = Activator.CreateInstance(factory.OptionsType)!;
            DtPipe.Cli.Infrastructure.TransformerArgsBinder.Bind(instance, pairs);
            var config = OptionObjectExporter.ExportTransformerConfig(factory.ComponentName, instance);
            if (config != null)
                configs.Add(config);
        }
        return configs.Count > 0 ? configs : null;
    }

    /// <summary>
    /// Rebuilds provider-options entries from stage-scoped reader/writer args, mirroring
    /// the YAML conventions: plain component key for the reader,
    /// <c>&lt;component&gt;-writer</c> for the writer. Only values that differ from the
    /// options defaults are emitted. A detected stream processor contributes its own
    /// payload under its component name (e.g. <c>sql</c>, <c>merge</c>).
    /// </summary>
    private static Dictionary<string, Dictionary<string, object>>? BuildProviderOptions(
        string? input, string? output,
        string[] readerArgs, string[] writerArgs,
        IEnumerable<IStreamReaderFactory>? readerFactories,
        IEnumerable<IDataWriterFactory>? writerFactories,
        IStreamTransformerFactory? processor = null,
        string[]? branchRawArgs = null)
    {
        if (readerFactories == null && writerFactories == null && processor == null)
            return null;

        var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        var readerFactory = ResolveFactory(input, readerFactories);
        if (readerFactory != null && readerArgs is { Length: > 0 })
        {
            var entry = BindToOptionDictionary(readerFactory.OptionsType, readerArgs, readerFactory.ComponentName);
            if (entry is { Count: > 0 })
                result[readerFactory.ComponentName] = entry.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
        }

        var writerFactory = ResolveFactory(output, writerFactories);
        if (writerFactory != null && writerArgs is { Length: > 0 })
        {
            var entry = BindToOptionDictionary(writerFactory.OptionsType, writerArgs, writerFactory.ComponentName);
            if (entry is { Count: > 0 })
                result[writerFactory.ComponentName + "-writer"] = entry.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
        }

        if (processor != null && branchRawArgs != null)
        {
            var payload = processor.ExportToProviderOptions(branchRawArgs);
            if (payload != null)
                result[processor.ComponentName] = payload;
        }

        return result.Count > 0 ? result : null;
    }

    private static T? ResolveFactory<T>(string? connectionString, IEnumerable<T>? factories)
        where T : class, IDataFactory
    {
        if (factories == null || string.IsNullOrEmpty(connectionString))
            return null;

        var raw = connectionString.Trim();
        foreach (var factory in factories)
        {
            var prefix = factory.ComponentName + ":";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                raw.Equals(factory.ComponentName, StringComparison.OrdinalIgnoreCase))
                return factory;
        }
        return factories.FirstOrDefault(f => f.CanHandle(raw));
    }

    private static Dictionary<string, string>? BindToOptionDictionary(Type optionsType, string[] args, string prefix)
    {
        object instance;
        try
        {
            instance = Activator.CreateInstance(optionsType)!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: cannot export provider options for '{prefix}': {ex.Message}");
            return null;
        }

        var registry = new FlagRegistry();
        foreach (var def in CliOptionBuilder.GenerateFlagDefsForType(optionsType))
            registry.Register(def);

        FlagBinder.Bind(instance, args, registry, prefix);
        return OptionObjectExporter.CollectChanged(instance);
    }

    // ── Helpers to extract typed values from flag dictionaries ──────────────

    private static string? GetString(IReadOnlyDictionary<string, List<string>> flags, params string[] keys)
    {
        foreach (var k in keys)
            if (flags.TryGetValue(k, out var list) && list.Count > 0) return list.Last();
        return null;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> flags, params string[] keys)
    {
        foreach (var k in keys)
            if (flags.TryGetValue(k, out var val)) return val?.ToString();
        return null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, List<string>> flags, params string[] keys)
    {
        var s = GetString(flags, keys);
        return s != null && int.TryParse(s, out var v) ? v : null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, object?> flags, params string[] keys)
    {
        var s = GetString(flags, keys);
        return s != null && int.TryParse(s, out var v) ? v : null;
    }

    private static int? GetNullableInt(IReadOnlyDictionary<string, List<string>> flags, params string[] keys)
        => GetInt(flags, keys);

    private static int? GetNullableInt(IReadOnlyDictionary<string, object?> flags, params string[] keys)
        => GetInt(flags, keys);

    private static double? GetDouble(IReadOnlyDictionary<string, List<string>> flags, params string[] keys)
    {
        var s = GetString(flags, keys);
        return s != null && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? GetDouble(IReadOnlyDictionary<string, object?> flags, params string[] keys)
    {
        var s = GetString(flags, keys);
        return s != null && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
