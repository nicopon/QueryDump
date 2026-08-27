using System;
using System.Linq;
using DtPipe.Cli.Infrastructure;
using DtPipe.Core.Abstractions;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

/// <summary>
/// Scheme-blind CanHandle regression guard: file-backed providers used to claim remote URIs by
/// extension alone ("s3://bucket/x.parquet" → Parquet writer), which silently wrote a LOCAL
/// directory literally named "s3:" instead of failing or reaching object storage.
///
/// Remote schemes are now claimed by exactly one pair of providers — "s3" and "azure", which
/// stream through the DuckDB engine. This test pins that allowlist in both directions: the
/// object-storage providers own their own schemes, and nothing else claims any scheme:// URI.
/// Widening it again must be a deliberate edit, not a silent side effect.
/// </summary>
public class RemoteUriClaimTests
{
    private static readonly string[] UnclaimedRemoteUris =
    {
        // No provider handles these schemes at all.
        "gs://bucket/data.parquet",
        "https://example.com/feed.jsonl",
        "http://example.com/file.csv",
        // Claimed schemes, but formats outside the closed extension map.
        "s3://bucket/archive.zip",
        "s3://bucket/sheet.xlsx",
        "azure://container/notes.txt",
        // Claimed schemes without a key: a container alone holds no bytes.
        "s3://bucket",
        "azure://container/",
    };

    private static (ComponentCatalog.CatalogEntry Entry, IDataFactory Factory)[] AllFactories()
    {
        var catalog = ComponentCatalog.Discover(
            typeof(DtPipe.Program).Assembly,
            typeof(DtPipe.Adapters.Csv.CsvReaderDescriptor).Assembly,
            typeof(DtPipe.Processors.Sql.CompositeSqlTransformerFactory).Assembly,
            typeof(DtPipe.Transformers.Services.JsEngineProvider).Assembly);

        return catalog.Readers.Concat(catalog.Writers)
            .Select(e => (Entry: e, Factory: (IDataFactory)Activator.CreateInstance(e.ImplementationType)!))
            .ToArray();
    }

    [Fact]
    public void Only_ObjectStorage_Providers_Claim_Remote_Scheme_Uris()
    {
        var factories = AllFactories();
        Assert.NotEmpty(factories);

        var claimed = factories
            .Where(pair => pair.Factory.CanHandle("s3://bucket/data.parquet")
                        || pair.Factory.CanHandle("azure://container/data.parquet"))
            .Select(pair => pair.Entry.ComponentName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "azure", "s3" }, claimed);
    }

    [Theory]
    [InlineData("s3://bucket/data.parquet", "s3")]
    [InlineData("s3a://bucket/data.csv", "s3")]
    [InlineData("azure://container/blob.jsonl", "azure")]
    [InlineData("az://container/blob.ndjson", "azure")]
    public void Claimed_Remote_Uri_Goes_To_Exactly_One_Provider(string uri, string expectedComponent)
    {
        var claimants = AllFactories()
            .Where(pair => pair.Factory.CanHandle(uri))
            .Select(pair => pair.Entry.ComponentName)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { expectedComponent }, claimants);
    }

    [Fact]
    public void No_Provider_Claims_Unsupported_Remote_Uris()
    {
        var factories = AllFactories();

        var violations = factories
            .Where(pair => UnclaimedRemoteUris.Any(uri => pair.Factory.CanHandle(uri)))
            .Select(pair => $"{pair.Entry.ComponentName} ({pair.Factory.OptionsType.Name})")
            .Distinct()
            .ToList();

        Assert.True(violations.Count == 0,
            "Providers claiming unsupported remote URIs would fail late or write local files. " +
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
