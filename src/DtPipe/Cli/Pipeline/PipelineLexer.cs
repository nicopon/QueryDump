using System;
using System.Collections.Generic;
using System.Linq;
using DtPipe.Core.Pipelines;

namespace DtPipe.Cli.Pipeline;

/// <summary>
/// Sequential lexer for DtPipe pipelines.
/// Branches are split implicitly by the second occurrence of -i/--input or by any --from flag.
/// All flags belong to the branch in which they appear, with strict stage-scoping enforced by
/// BuildBranch: flags must appear in the correct stage (reader before transformers, writer after -o).
/// </summary>
public class PipelineLexer
{
    private readonly FlagRegistry _registry;

    public PipelineLexer(FlagRegistry registry)
    {
        _registry = registry;
    }

    public ParsedPipeline Parse(string[] args)
    {
        var globalDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var branches = new List<BranchSpec>();

        var currentBranchFlags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var currentBranchArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];

            var def = _registry.Lookup(token);
            if (def != null)
            {
                string? value = null;
                if (def.ConsumesNextToken)
                {
                    // F8 arity-driven consumption: scalar/repeatable flags always take the
                    // next token as their value — even dash-leading ones ("-5", "-###-").
                    // This is the same rule OptionBinder.BindCli applies to raw args.
                    if (i + 1 < args.Length)
                        value = args[++i];
                }

                // Implicit branch-split: a second -i, any --from, or a second --job triggers a new branch.
                bool alreadyHasInput = currentBranchFlags.ContainsKey("--input") || currentBranchFlags.ContainsKey("-i");
                bool alreadyHasFrom  = currentBranchFlags.ContainsKey("--from");
                bool alreadyHasJob   = currentBranchFlags.ContainsKey("--job") || currentBranchFlags.ContainsKey("-j");

                bool isNewInput = (def.Name == "--input" || def.Name == "-i") && (alreadyHasInput || alreadyHasJob);
                bool isNewFrom  = (def.Name == "--from")  && (alreadyHasFrom || alreadyHasInput || alreadyHasJob);
                bool isNewJob   = (def.Name == "--job" || def.Name == "-j") && (alreadyHasJob || alreadyHasInput);

                if (isNewInput || isNewFrom || isNewJob)
                {
                    branches.Add(BuildBranch(currentBranchFlags, currentBranchArgs));
                    currentBranchFlags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    currentBranchArgs  = new List<string>();
                }

                // Global flags go to globalDict only.
                // Per-branch flags go to both globalDict (for global defaults) and the current branch.
                globalDict[def.Name] = value ?? "true";
                if (def.Scope == FlagScope.PerBranch)
                {
                    // F17: a repeated non-repeatable flag silently kept only the last
                    // occurrence — surface it (last-wins behavior is unchanged).
                    if (def.Arity != FlagArity.Repeatable && currentBranchFlags.ContainsKey(def.Name))
                        WarnRepeated(token);

                    if (!currentBranchFlags.ContainsKey(def.Name)) currentBranchFlags[def.Name] = new List<string>();
                    currentBranchFlags[def.Name].Add(value ?? "true");
                    currentBranchArgs.Add(token);
                    if (value != null) currentBranchArgs.Add(value);
                }
            }
            else
            {
                if (token.StartsWith('-'))
                {
                    // Unknown flag — store as boolean, captured in RawArgs for OptionBinder.
                    globalDict[token] = "true";
                    if (currentBranchFlags.ContainsKey(token))
                        WarnRepeated(token);
                    if (!currentBranchFlags.ContainsKey(token)) currentBranchFlags[token] = new List<string>();
                    currentBranchFlags[token].Add("true");
                    currentBranchArgs.Add(token);
                }
                else
                {
                    // Positional token (SQL query without --sql flag).
                    // Split the reader into its own branch before the SQL processor branch.
                    if (currentBranchArgs.Count > 0 && !currentBranchFlags.ContainsKey("--from"))
                    {
                        branches.Add(BuildBranch(currentBranchFlags, currentBranchArgs));
                        currentBranchFlags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        currentBranchArgs  = new List<string>();
                    }
                    if (!currentBranchFlags.ContainsKey("--sql")) currentBranchFlags["--sql"] = new List<string>();
                    currentBranchFlags["--sql"].Add(token);
                    currentBranchArgs.Add("--sql");
                    currentBranchArgs.Add(token);
                }
            }
        }

        if (currentBranchArgs.Count > 0)
            branches.Add(BuildBranch(currentBranchFlags, currentBranchArgs));

        return new ParsedPipeline(MapGlobals(globalDict), branches);
    }

    private static void WarnRepeated(string token)
        => Console.Error.WriteLine($"[dtpipe] Warning: flag '{token}' appears more than once in the same branch; using the last occurrence.");

    private GlobalOptions MapGlobals(Dictionary<string, object?> dict)
    {
        return new GlobalOptions
        {
            DryRunCount   = GetDryRun(dict),
            NoStats       = dict.ContainsKey("--no-stats"),
            StrictBindings= dict.ContainsKey("--strict-bindings"),
            LogPath       = dict.TryGetValue("--log", out var logVal) ? logVal?.ToString() : null,
            JobFile       = dict.TryGetValue("--job", out var jobVal) ? jobVal?.ToString()
                          : dict.TryGetValue("-j", out var jVal) ? jVal?.ToString() : null,
            ExportJobFile = dict.TryGetValue("--export-job", out var ejVal) ? ejVal?.ToString() : null,
            IgnoreNulls   = dict.ContainsKey("--ignore-nulls"),
            AllFlags      = dict
        };
    }

    private int GetDryRun(Dictionary<string, object?> dict)
    {
        if (dict.TryGetValue("--dry-run", out var val))
        {
            if (val is string s && int.TryParse(s, out var i)) return i;
            return 1;
        }
        if (dict.TryGetValue("-dr", out val))
        {
            if (val is string s && int.TryParse(s, out var i)) return i;
            return 1;
        }
        return 0;
    }

    private BranchSpec BuildBranch(Dictionary<string, List<string>> flags, List<string> rawArgs)
    {
        string? GetSingle(params string[] keys)
        {
            foreach (var k in keys)
                if (flags.TryGetValue(k, out var list)) return list.LastOrDefault();
            return null;
        }

        string[] GetList(params string[] keys)
        {
            var result = new List<string>();
            foreach (var k in keys)
                if (flags.TryGetValue(k, out var list)) result.AddRange(list);
            return result.ToArray();
        }

        // ── Stage-scoped arg splitting ─────────────────────────────────────────────
        // writer boundary: first -o / --output
        int writerStart = -1;
        for (int idx = 0; idx < rawArgs.Count; idx++)
            if (rawArgs[idx] == "-o" || rawArgs[idx] == "--output") { writerStart = idx; break; }

        // pipeline boundary: first flag with FlagStage == Pipeline exactly (transformer trigger)
        int pipelineStart = -1;
        int searchEnd = writerStart >= 0 ? writerStart : rawArgs.Count;
        for (int idx = 0; idx < searchEnd; idx++)
        {
            var def = _registry.Lookup(rawArgs[idx]);
            if (def?.Stage == FlagStage.Pipeline) { pipelineStart = idx; break; }
        }

        int readerEnd   = pipelineStart >= 0 ? pipelineStart : (writerStart >= 0 ? writerStart : rawArgs.Count);
        int pipelineEnd = writerStart >= 0 ? writerStart : rawArgs.Count;

        var readerArgs   = rawArgs.Take(readerEnd).ToArray();
        var pipelineArgs = pipelineStart >= 0
            ? rawArgs.Skip(pipelineStart).Take(pipelineEnd - pipelineStart).ToArray()
            : Array.Empty<string>();
        var writerArgs   = writerStart >= 0 ? rawArgs.Skip(writerStart).ToArray() : Array.Empty<string>();

        // ── Stage validation ────────────────────────────────────────────────────────
        ValidateStageConstraints(readerArgs,   FlagStage.Reader,   "before the first transformer or -o");
        ValidateStageConstraints(writerArgs,   FlagStage.Writer,   "after -o");
        ValidateStageConstraints(pipelineArgs, FlagStage.Pipeline, "in transformer scope (between transformers and -o)");

        return new BranchSpec
        {
            Input  = GetSingle("--input", "-i"),
            Output = GetSingle("--output", "-o"),
            Alias  = GetSingle("--alias"),
            From   = GetList("--from").SelectMany(s => s.Split(',')).Select(s => s.Trim()).ToList(),
            Ref    = GetList("--ref").SelectMany(s => s.Split(',')).Select(s => s.Trim()).ToList(),

            ReaderArgs   = readerArgs,
            PipelineArgs = pipelineArgs,
            WriterArgs   = writerArgs,

            RawArgs = rawArgs.ToArray(),
            Flags   = flags
        };
    }

    private void ValidateStageConstraints(string[] args, FlagStage requiredStage, string stageName)
    {
        foreach (var token in args)
        {
            if (!token.StartsWith('-')) continue;
            var def = _registry.Lookup(token);
            if (def == null) continue;
            if (!def.Stage.HasFlag(requiredStage))
                throw new InvalidOperationException(
                    $"Flag '{token}' (valid in: {def.Stage}) cannot appear {stageName}. " +
                    $"Group flags with their component: reader flags before transformers, writer flags after -o.");
        }
    }
}
