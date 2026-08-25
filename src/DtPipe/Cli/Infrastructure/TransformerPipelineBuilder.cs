using System;
using System.Collections.Generic;
using System.Linq;
using DtPipe.Cli.Pipeline;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Pipelines;

namespace DtPipe.Cli.Infrastructure;

/// <summary>
/// Dynamically builds a transformer pipeline from CLI arguments.
/// Matches arguments to registered transformer factories and groups their options.
/// Uses FlagDef (from ICliContributor.GetFlagDefs()) instead of System.CommandLine Option.
/// </summary>
public class TransformerPipelineBuilder
{
    private readonly IEnumerable<IDataTransformerFactory> _factories;

    public TransformerPipelineBuilder(IEnumerable<IDataTransformerFactory> factories)
    {
        _factories = factories;
    }

    public List<IDataTransformer> Build(string[] args)
    {
        var pipeline = new List<IDataTransformer>();
        foreach (var (factory, pairs) in CollectGroups(args))
        {
            // Same binding path as the live CLI: bind the flag group into a fresh options
            // instance, then let the factory create the transformer from it.
            var instance = Activator.CreateInstance(factory.OptionsType)!;
            OptionBinder.BindPairs(instance, pairs);
            var transformer = factory.CreateFromOptions(instance);
            if (transformer != null) pipeline.Add(transformer);
        }
        return pipeline;
    }

    /// <summary>
    /// Groups raw args into ordered transformer flag groups using the canonical rule:
    /// consecutive options belonging to the same factory form one group; re-occurrence of
    /// the factory's trigger flag starts a new group of the same factory.
    /// Single source shared by live execution (<see cref="Build"/>) and --export-job.
    /// </summary>
    public List<(IDataTransformerFactory Factory, List<(string Option, string Value)> Pairs)> CollectGroups(string[] args)
    {
        var groups = new List<(IDataTransformerFactory, List<(string, string)>)>();
        var transformerFactories = _factories.ToList();

        // Build option maps
        var globalOptionMap = new Dictionary<string, (IDataTransformerFactory Factory, FlagDef Flag)>(StringComparer.OrdinalIgnoreCase);
        var perFactoryMap = new Dictionary<IDataTransformerFactory, Dictionary<string, FlagDef>>();

        foreach (var factory in transformerFactories)
        {
            var flags = (factory is ICliContributor contributor)
                ? contributor.GetFlagDefs()
                : CliOptionBuilder.GenerateFlagDefsForType(factory.OptionsType);

            var factoryDict = new Dictionary<string, FlagDef>(StringComparer.OrdinalIgnoreCase);
            perFactoryMap[factory] = factoryDict;

            foreach (var flag in flags)
            {
                if (!string.IsNullOrEmpty(flag.Name))
                {
                    globalOptionMap[flag.Name] = (factory, flag);
                    factoryDict[flag.Name] = flag;
                }
                foreach (var alias in flag.Aliases)
                {
                    globalOptionMap[alias] = (factory, flag);
                    factoryDict[alias] = flag;
                }
            }
        }

        // Group consecutive args by transformer type
        IDataTransformerFactory? currentFactory = null;
        var currentOptions = new List<(string Key, string Value)>();

        void FlushCurrent()
        {
            if (currentFactory != null && currentOptions.Count > 0)
                groups.Add((currentFactory, currentOptions));
            currentOptions = new List<(string Key, string Value)>();
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 1. Contextual lookup: prefer current factory if it supports the flag
            (IDataTransformerFactory Factory, FlagDef Flag)? match = null;
            if (currentFactory != null && perFactoryMap[currentFactory].TryGetValue(arg, out var currentFlag))
            {
                // Trigger Detection: if this is the "trigger" flag (matching prefix)
                // and we already have it in currentOptions, it starts a new instance.
                var triggerName = $"--{currentFactory.ComponentName.ToLowerInvariant()}";
                bool isTrigger = string.Equals(arg, triggerName, StringComparison.OrdinalIgnoreCase);

                if (isTrigger && currentOptions.Any(o => string.Equals(o.Key, arg, StringComparison.OrdinalIgnoreCase)))
                {
                    // Force flush to start new instance of the same factory
                    FlushCurrent();
                }

                match = (currentFactory, currentFlag);
            }
            // 2. Global lookup
            else if (globalOptionMap.TryGetValue(arg, out var globalMatch))
            {
                match = globalMatch;
            }

            if (match != null)
            {
                var factory = match.Value.Factory;
                var flag = match.Value.Flag;

                if (factory != currentFactory && currentFactory != null && currentOptions.Count > 0)
                {
                    FlushCurrent();
                }

                currentFactory = factory;

                // Determine if we should consume a value — F8 arity-driven rule:
                // scalar/repeatable flags always take the next token, even dash-leading
                // (e.g. --mask "-###-").
                string? value = null;
                if (flag.ConsumesNextToken)
                {
                    if (i + 1 < args.Length)
                    {
                        value = args[++i];
                    }
                    else if (flag.Arity == FlagArity.Scalar)
                    {
                         // Missing required value for scalar
                         throw new InvalidOperationException($"Flag '{arg}' requires a value.");
                    }
                }
                else
                {
                    value = "true";
                }

                if (value != null)
                {
                    currentOptions.Add((arg, value));
                }
            }
        }

        // Flush last
        FlushCurrent();

        return groups;
    }
}
