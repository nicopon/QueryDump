using System;
using System.Linq;
using DtPipe.Cli.Infrastructure;
using DtPipe.Core.Abstractions;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

/// <summary>
/// Scheme-blind CanHandle regression guard: file-backed providers used to claim
/// remote URIs by extension alone ("s3://bucket/x.parquet" → Parquet writer), which
/// silently wrote a LOCAL directory literally named "s3:" instead of failing or
/// reaching object storage. No provider may claim a "scheme://" connection string
/// today; object storage goes through the DuckDB provider (httpfs / azure extension).
/// If a future provider legitimately handles remote URIs, this test must be updated
/// consciously — not bypassed.
/// </summary>
public class RemoteUriClaimTests
{
    private static readonly string[] RemoteUris =
    {
        "s3://dtpipe-test-bucket/users.parquet",
        "s3a://bucket/key.csv",
        "azure://container/blob.jsonl",
        "az://container/blob.arrow",
        "gs://bucket/data.xml",
        "https://example.com/feed.jsonl",
        "http://example.com/file.csv",
    };

    [Fact]
    public void No_Provider_Claims_Remote_Scheme_Uris()
    {
        var catalog = ComponentCatalog.Discover(
            typeof(DtPipe.Program).Assembly,
            typeof(DtPipe.Adapters.Csv.CsvReaderDescriptor).Assembly,
            typeof(DtPipe.Processors.Sql.CompositeSqlTransformerFactory).Assembly,
            typeof(DtPipe.Transformers.Services.JsEngineProvider).Assembly);

        var factories = catalog.Readers.Concat(catalog.Writers)
            .Select(e => (Entry: e, Factory: (IDataFactory)Activator.CreateInstance(e.ImplementationType)!))
            .ToList();

        Assert.NotEmpty(factories);

        var violations = factories
            .Where(pair => RemoteUris.Any(uri => pair.Factory.CanHandle(uri)))
            .Select(pair => $"{pair.Entry.ComponentName} ({pair.Factory.OptionsType.Name})")
            .Distinct()
            .ToList();

        Assert.True(violations.Count == 0,
            "Providers claiming remote scheme:// URIs would silently write local files. " +
            "Violations: " + string.Join(", ", violations));
    }

    [Theory]
    [InlineData("s3://bucket/data.parquet")]
    [InlineData("azure://c/b.parquet")]
    [InlineData("C:\\data\\export.parquet")]
    [InlineData("csv:data.parquet")]
    public void Parquet_Writer_Claims_Local_Paths_Not_Remote_Uris(string connectionString)
    {
        // Positive + negative control around the Windows-drive and provider-prefix edge cases.
        var expected = !connectionString.Contains("://", StringComparison.Ordinal);
        var claimed = new DtPipe.Adapters.Parquet.ParquetWriterDescriptor().CanHandle(connectionString);
        Assert.Equal(expected, claimed);
    }
}
