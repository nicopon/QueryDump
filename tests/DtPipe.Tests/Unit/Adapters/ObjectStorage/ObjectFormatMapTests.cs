using System;
using System.Collections.Generic;
using DtPipe.Adapters.Common;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.ObjectStorage;

public class ObjectFormatMapTests
{
    private static readonly IReadOnlySet<string> S3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s3" };

    [Theory]
    [InlineData("s3://b/k.parquet", "read_parquet", "PARQUET")]
    [InlineData("s3://b/k.csv", "read_csv", "CSV")]
    [InlineData("s3://b/k.tsv", "read_csv", "CSV")]
    [InlineData("s3://b/k.json", "read_json", "JSON")]
    [InlineData("s3://b/k.jsonl", "read_json", "JSON")]
    [InlineData("s3://b/k.ndjson", "read_json", "JSON")]
    public void Resolve_Maps_Extension_To_DuckDB_Functions(string uri, string readFn, string copyFormat)
    {
        var spec = ObjectFormatMap.Resolve(ObjectUri.Parse(uri, S3));

        Assert.Equal(readFn, spec.ReadFunction);
        Assert.Equal(copyFormat, spec.CopyFormat);
    }

    /// <summary>
    /// The map is closed on purpose: guessing a format from an unknown extension would surface as
    /// an opaque DuckDB failure mid-run instead of an actionable message up front.
    /// </summary>
    [Theory]
    [InlineData("s3://b/k.avro")]
    [InlineData("s3://b/k.xlsx")]
    [InlineData("s3://b/k.zip")]
    [InlineData("s3://b/k")]
    public void Resolve_Rejects_Unknown_Extensions_And_Lists_Supported(string uri)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ObjectFormatMap.Resolve(ObjectUri.Parse(uri, S3)));

        Assert.Contains("supported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".parquet", ex.Message);
    }
}
