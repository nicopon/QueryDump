using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Pipelines.Dag;
using DtPipe.Processors;
using DtPipe.Processors.DuckDB;
using DtPipe.Processors.Merge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DtPipe.Tests.Unit.Processors;

public class SqlTransformerFactoryTests
{
    private readonly DuckDBSqlTransformerFactory _factory = new();

    [Fact]
    public void IsApplicable_WithSqlFlag_ReturnsTrue()
    {
        var args = new[] { "--from", "src", "--sql", "SELECT * FROM src" };
        Assert.True(_factory.IsApplicable(args));
    }

    [Fact]
    public void IsApplicable_WithoutSqlFlag_ReturnsFalse()
    {
        var args = new[] { "--from", "src", "--merge" };
        Assert.False(_factory.IsApplicable(args));
    }

    // ── YAML (JobDefinition) surface ──

    [Fact]
    public void IsApplicable_Job_WithSqlProviderOptions_ReturnsTrue()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition
        {
            From = "src",
            ProviderOptions = new() { ["sql"] = new() { ["query"] = "SELECT 1" } }
        };
        Assert.True(f.IsApplicable(job));
    }

    [Fact]
    public void IsApplicable_Job_WithoutSql_ReturnsFalse()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition { ProviderOptions = new() { ["merge"] = new() } };
        Assert.False(f.IsApplicable(job));
    }

    [Fact]
    public void CreateFromJob_Sql_MissingQuery_Throws()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition { From = "src", ProviderOptions = new() { ["sql"] = new() } };
        Assert.Throws<ArgumentException>(() =>
            f.CreateFromJob(job, new BranchChannelContext(), new ServiceCollection().BuildServiceProvider()));
    }
}

public class MergeTransformerFactoryTests
{
    private readonly MergeTransformerFactory _factory = new();

    [Fact]
    public void IsApplicable_WithMergeFlag_ReturnsTrue()
    {
        var args = new[] { "--from", "a,b", "--merge" };
        Assert.True(_factory.IsApplicable(args));
    }

    [Fact]
    public void IsApplicable_WithoutMergeFlag_ReturnsFalse()
    {
        var args = new[] { "--from", "a,b", "--sql", "SELECT * FROM a" };
        Assert.False(_factory.IsApplicable(args));
    }

    [Fact]
    public void IsApplicable_WithMergeFlagCaseInsensitive_ReturnsTrue()
    {
        var args = new[] { "--from", "a,b", "--MERGE" };
        Assert.True(_factory.IsApplicable(args));
    }

    // ── YAML (JobDefinition) surface ──

    [Fact]
    public void IsApplicable_Job_WithMergeProviderOptions_ReturnsTrue()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition { From = "a,b", ProviderOptions = new() { ["merge"] = new() } };
        Assert.True(f.IsApplicable(job));
    }

    [Fact]
    public void IsApplicable_Job_WithoutMerge_ReturnsFalse()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition { ProviderOptions = new() { ["sql"] = new() { ["query"] = "SELECT 1" } } };
        Assert.False(f.IsApplicable(job));
    }

    [Fact]
    public void CreateFromJob_Merge_SingleSource_Throws()
    {
        IStreamTransformerFactory f = _factory;
        var job = new JobDefinition { From = "only", ProviderOptions = new() { ["merge"] = new() } };
        Assert.Throws<ArgumentException>(() =>
            f.CreateFromJob(job, new BranchChannelContext(), new ServiceCollection().BuildServiceProvider()));
    }
}

public class BranchArgParserTests
{
    [Fact]
    public void ExtractValue_ReturnsValueAfterFlag()
    {
        var args = new[] { "--sql", "SELECT 1" };
        Assert.Equal("SELECT 1", BranchArgParser.ExtractValue(args, "--sql"));
    }

    [Fact]
    public void ExtractValue_MissingFlag_ReturnsNull()
    {
        var args = new[] { "--from", "src" };
        Assert.Null(BranchArgParser.ExtractValue(args, "--sql"));
    }

    [Fact]
    public void ExtractValue_FlagAtEnd_ReturnsNull()
    {
        var args = new[] { "--from", "src", "--sql" };
        Assert.Null(BranchArgParser.ExtractValue(args, "--sql"));
    }

    [Fact]
    public void ExtractAllValues_MultipleOccurrences_ReturnsAll()
    {
        var args = new[] { "--ref", "a", "--ref", "b", "--ref", "c" };
        var values = BranchArgParser.ExtractAllValues(args, "--ref").ToList();
        Assert.Equal(["a", "b", "c"], values);
    }

    [Fact]
    public void ExtractAllValues_NoOccurrence_ReturnsEmpty()
    {
        var args = new[] { "--from", "src", "--sql", "SELECT 1" };
        Assert.Empty(BranchArgParser.ExtractAllValues(args, "--ref"));
    }
}
