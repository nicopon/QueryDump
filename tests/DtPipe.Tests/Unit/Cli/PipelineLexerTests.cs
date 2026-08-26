using DtPipe.Cli.Pipeline;
using Xunit;
using System.Linq;

namespace DtPipe.Tests.Unit.Cli;

public class PipelineLexerTests
{
    private readonly FlagRegistry _registry;
    private readonly PipelineLexer _lexer;

    public PipelineLexerTests()
    {
        _registry = new FlagRegistry();
        CoreFlagRegistry.RegisterCoreFlags(_registry);

        // Engine controls (FlagStage.All — valid in any position)
        foreach (var def in new DtPipe.Cli.Infrastructure.PipelineOptionsCliContributor().GetFlagDefs())
            _registry.Register(def with { Stage = FlagStage.All });

        // Reader-specific flags (FlagStage.Reader)
        _registry.Register(new FlagDef("--fake", new[] { "-f" }, FlagArity.Scalar, FlagScope.PerBranch, "fake transformer", FlagStage.Pipeline));
        _registry.Register(new FlagDef("--sql",  Array.Empty<string>(), FlagArity.Scalar,  FlagScope.PerBranch, "sql processor",   FlagStage.Pipeline));
        _registry.Register(new FlagDef("--merge", Array.Empty<string>(), FlagArity.Boolean, FlagScope.PerBranch, "merge processor", FlagStage.Pipeline));

        // Shared reader+writer flags (FlagStage.Any = Reader | Writer)
        _registry.Register(new FlagDef("--table", new[] { "-t" }, FlagArity.Scalar, FlagScope.PerBranch, "table", FlagStage.Any));
        _registry.Register(new FlagDef("--strategy", new[] { "-s" }, FlagArity.Scalar, FlagScope.PerBranch, "strategy", FlagStage.Any));

        _lexer = new PipelineLexer(_registry);
    }

    // ── Linear pipelines ───────────────────────────────────────────────

    [Fact]
    public void Parse_LinearSimple_ReturnsOneBranch()
    {
        var args = new[] { "-i", "gen:10", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Single(pipeline.Branches);
        Assert.Equal("gen:10", pipeline.Branches[0].Input);
        Assert.Equal("out.csv", pipeline.Branches[0].Output);
    }

    [Fact]
    public void Parse_WithTransformers_ReturnsCorrectBranch()
    {
        var args = new[] { "-i", "gen:10", "--fake", "Id:random.uuid", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Single(pipeline.Branches);
        Assert.Equal("gen:10", pipeline.Branches[0].Input);
        Assert.Equal("out.csv", pipeline.Branches[0].Output);
        Assert.Contains("--fake", pipeline.Branches[0].RawArgs);
        Assert.Contains("Id:random.uuid", pipeline.Branches[0].RawArgs);
    }

    // ── Stage splitting ────────────────────────────────────────────────

    [Fact]
    public void Parse_TransformerFlagSplitsStages()
    {
        var args = new[] { "-i", "gen:10", "--fake", "Id:random.uuid", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Single(pipeline.Branches);
        var branch = pipeline.Branches[0];

        // Reader scope: everything before --fake
        Assert.Contains("-i", branch.ReaderArgs);
        Assert.Contains("gen:10", branch.ReaderArgs);
        Assert.DoesNotContain("--fake", branch.ReaderArgs);

        // Pipeline scope: transformer and its value
        Assert.Contains("--fake", branch.PipelineArgs);
        Assert.Contains("Id:random.uuid", branch.PipelineArgs);
        Assert.DoesNotContain("-o", branch.PipelineArgs);

        // Writer scope: from -o to end
        Assert.Contains("-o", branch.WriterArgs);
        Assert.Contains("out.csv", branch.WriterArgs);
        Assert.DoesNotContain("--fake", branch.WriterArgs);
    }

    [Fact]
    public void Parse_NoTransformer_AllArgsSplitBetweenReaderAndWriter()
    {
        var args = new[] { "-i", "gen:5", "--table", "src", "-o", "out.csv", "--table", "tgt", "--strategy", "Recreate" };
        var pipeline = _lexer.Parse(args);

        var branch = pipeline.Branches[0];
        Assert.Contains("--table", branch.ReaderArgs);   // --table before -o → reader scope
        Assert.Contains("src", branch.ReaderArgs);
        Assert.Empty(branch.PipelineArgs);
        Assert.Contains("--table", branch.WriterArgs);   // --table after -o → writer scope
        Assert.Contains("tgt", branch.WriterArgs);
        Assert.Contains("--strategy", branch.WriterArgs);
    }

    [Fact]
    public void Parse_GlobalBatchSize_CapturedInGlobals()
    {
        var args = new[] { "--batch-size", "1000", "--limit", "5000", "-i", "gen:10", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Equal("1000", pipeline.Globals.AllFlags["--batch-size"]?.ToString());
        Assert.Equal("5000", pipeline.Globals.AllFlags["--limit"]?.ToString());
    }

    // ── Stage validation (strict) ──────────────────────────────────────

    [Fact]
    public void Parse_WriterFlagInPipelineScope_Throws()
    {
        // --table is FlagStage.Any (Reader|Writer), not Pipeline → error after --fake
        var args = new[] { "-i", "gen:5", "--fake", "col:random.uuid", "--table", "wrong", "-o", "out.csv" };
        Assert.Throws<InvalidOperationException>(() => _lexer.Parse(args));
    }

    [Fact]
    public void Parse_TransformerFlagInWriterScope_Throws()
    {
        // --fake is FlagStage.Pipeline → error after -o
        var args = new[] { "-i", "gen:5", "-o", "out.csv", "--fake", "col:random.uuid" };
        Assert.Throws<InvalidOperationException>(() => _lexer.Parse(args));
    }

    // ── Implicit branch splitting ──────────────────────────────────────

    [Fact]
    public void Parse_TwoInputs_ReturnsTwoBranches()
    {
        var args = new[] { "-i", "a.csv", "-o", "out1.csv", "-i", "b.csv", "-o", "out2.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Equal(2, pipeline.Branches.Count);
        Assert.Equal("a.csv", pipeline.Branches[0].Input);
        Assert.Equal("b.csv", pipeline.Branches[1].Input);
    }

    [Fact]
    public void Parse_FanOut_ReturnsThreeBranches()
    {
        // Source branch (1), two consumer branches split by --from
        var args = new[] { "-i", "a.csv", "--alias", "s", "--from", "s", "-o", "out1.csv", "--from", "s", "-o", "out2.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Equal(3, pipeline.Branches.Count);
        Assert.Equal("a.csv",   pipeline.Branches[0].Input);
        Assert.Equal("s",       pipeline.Branches[0].Alias);
        Assert.Equal("s",       pipeline.Branches[1].From[0]);
        Assert.Equal("out1.csv",pipeline.Branches[1].Output);
        Assert.Equal("s",       pipeline.Branches[2].From[0]);
        Assert.Equal("out2.csv",pipeline.Branches[2].Output);
    }

    [Fact]
    public void Parse_SqlProcessor_ReturnsCorrectTopology()
    {
        var args = new[] { "-i", "a.csv", "--alias", "src", "--from", "src", "--sql", "SELECT * FROM src", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Equal(2, pipeline.Branches.Count);
        Assert.Equal("a.csv", pipeline.Branches[0].Input);
        Assert.Equal("src",   pipeline.Branches[0].Alias);

        Assert.Equal("src", pipeline.Branches[1].From[0]);
        Assert.Contains("--sql",            pipeline.Branches[1].RawArgs);
        Assert.Contains("SELECT * FROM src", pipeline.Branches[1].RawArgs);
        Assert.Equal("out.csv", pipeline.Branches[1].Output);
    }

    [Fact]
    public void Parse_SqlWithRef_ReturnsCorrectTopology()
    {
        var args = new[] {
            "-i", "a.csv", "--alias", "m",
            "-i", "b.csv", "--alias", "r",
            "--from", "m", "--ref", "r", "--sql", "SELECT * FROM m JOIN r", "-o", "out.csv"
        };
        var pipeline = _lexer.Parse(args);

        Assert.Equal(3, pipeline.Branches.Count);
        Assert.Equal("m",   pipeline.Branches[2].From[0]);
        Assert.Equal("r",   pipeline.Branches[2].Ref[0]);
        Assert.Contains("SELECT * FROM m JOIN r", pipeline.Branches[2].RawArgs);
    }

    [Fact]
    public void Parse_Merge_ReturnsCorrectTopology()
    {
        var args = new[] {
            "-i", "a.csv", "--alias", "a",
            "-i", "b.csv", "--alias", "b",
            "--from", "a,b", "--merge", "-o", "out.csv"
        };
        var pipeline = _lexer.Parse(args);

        Assert.Equal(3, pipeline.Branches.Count);
        Assert.Contains("--merge", pipeline.Branches[2].RawArgs);
        Assert.Equal(new[] { "a", "b" }, pipeline.Branches[2].From.ToArray());
    }

    // ── Dry-run and global flags ───────────────────────────────────────

    [Fact]
    public void Parse_DryRun_HandledCorrectly()
    {
        var pipeline1 = _lexer.Parse(new[] { "--dry-run", "-i", "gen:5" });
        Assert.Equal(1, pipeline1.Globals.DryRunCount);

        var pipeline2 = _lexer.Parse(new[] { "--dry-run", "5", "-i", "gen:5" });
        Assert.Equal(5, pipeline2.Globals.DryRunCount);

        var pipeline3 = _lexer.Parse(new[] { "-dr", "10", "-i", "gen:5" });
        Assert.Equal(10, pipeline3.Globals.DryRunCount);
    }

    [Fact]
    public void Parse_UnknownFlag_StoredAsBoolean()
    {
        var args = new[] { "-i", "gen:10", "--custom-flag", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Contains("--custom-flag", pipeline.Branches[0].RawArgs);
    }

    [Fact]
    public void Parse_PositionalSql_HandledCorrectly()
    {
        var args = new[] { "-i", "a.csv", "--alias", "s", "--from", "s", "SELECT * FROM s", "-o", "out.csv" };
        var pipeline = _lexer.Parse(args);

        Assert.Contains("SELECT * FROM s", pipeline.Branches[1].RawArgs);
    }
}

[Collection("console-serial")]
public class RepeatedFlagStrictnessTests
{
    private static PipelineLexer BuildLexer()
    {
        var registry = new FlagRegistry();
        CoreFlagRegistry.RegisterCoreFlags(registry);
        // Mirror runtime registration of the CSV flags used by these tests. At runtime the
        // reader AND writer contributors both register --csv-separator, merging Stage to
        // Reader|Writer (FlagStage.Any) — cross-stage repeats are then legitimate.
        foreach (var def in DtPipe.Cli.Infrastructure.CliOptionBuilder.GenerateFlagDefsForType(typeof(DtPipe.Adapters.Csv.CsvReaderOptions)))
            registry.Register(def with { Stage = FlagStage.Any });
        return new PipelineLexer(registry);
    }

    private static string ParseCapturingStderr(params string[] args)
    {
        var lexer = BuildLexer();

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            lexer.Parse(args);
        }
        finally
        {
            Console.SetError(originalError);
        }
        return captured.ToString();
    }

    [Fact]
    public void Repeated_Scalar_Flag_In_Same_Stage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseCapturingStderr("-i", "in.csv", "--csv-separator", ";", "--csv-separator", "|", "-o", "out.csv"));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--csv-separator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_Stage_Repeat_Is_Allowed()
    {
        // Reader separator and writer separator are independent bindings in one branch.
        var pipeline = BuildLexer().Parse(new[] { "-i", "in.csv", "--csv-separator", ";", "-o", "out.csv", "--csv-separator", "|" });
        Assert.Single(pipeline.Branches);
    }

    [Fact]
    public void Repeated_Global_Flag_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseCapturingStderr("-i", "in.csv", "--log", "a.log", "--log", "b.log", "-o", "out.csv"));
        Assert.Contains("--log", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Positional_Query_After_Explicit_Sql_In_From_Branch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseCapturingStderr("-i", "in.csv", "--alias", "s", "--from", "s", "--sql", "SELECT 1", "SELECT 2"));
        Assert.Contains("SQL query provided more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_Sql_After_Positional_Query_In_From_Branch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseCapturingStderr("-i", "in.csv", "--alias", "s", "--from", "s", "SELECT 1", "--sql", "SELECT 2"));
        Assert.Contains("SQL query provided more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_Flags_Do_Not_Warn()
    {
        var output = ParseCapturingStderr("-i", "in.csv", "-o", "out.csv", "--no-stats");
        Assert.DoesNotContain("Warning: flag", output, StringComparison.Ordinal);
    }
}
