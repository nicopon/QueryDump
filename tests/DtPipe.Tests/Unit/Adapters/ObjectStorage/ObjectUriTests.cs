using System;
using System.Collections.Generic;
using DtPipe.Adapters.Common;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.ObjectStorage;

public class ObjectUriTests
{
    private static readonly IReadOnlySet<string> S3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s3", "s3a" };
    private static readonly IReadOnlySet<string> Azure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azure", "az" };

    [Theory]
    [InlineData("s3://bucket/key.parquet", "bucket", "key.parquet", ".parquet")]
    [InlineData("s3://bucket/a/b/c.csv", "bucket", "a/b/c.csv", ".csv")]
    [InlineData("s3://bucket/dt=2026-08-26/part-0.parquet", "bucket", "dt=2026-08-26/part-0.parquet", ".parquet")]
    [InlineData("s3://bucket/UPPER.PARQUET", "bucket", "UPPER.PARQUET", ".parquet")]
    public void Parse_Splits_Container_Key_And_Extension(string input, string container, string key, string extension)
    {
        var uri = ObjectUri.Parse(input, S3);

        Assert.Equal(container, uri.Container);
        Assert.Equal(key, uri.Key);
        Assert.Equal(extension, uri.Extension);
    }

    /// <summary>
    /// The alternate spellings are accepted from users but never emitted into SQL: DuckDB's
    /// extensions only register the canonical scheme, so "s3a://" would not resolve.
    /// </summary>
    [Theory]
    [InlineData("s3a://bucket/key.parquet", "s3://bucket/key.parquet")]
    [InlineData("s3://bucket/key.parquet", "s3://bucket/key.parquet")]
    public void DuckDbUri_Normalises_Alternate_S3_Schemes(string input, string expected)
        => Assert.Equal(expected, ObjectUri.Parse(input, S3).DuckDbUri);

    [Theory]
    [InlineData("az://container/blob.parquet", "azure://container/blob.parquet")]
    [InlineData("azure://container/blob.parquet", "azure://container/blob.parquet")]
    public void DuckDbUri_Normalises_Alternate_Azure_Schemes(string input, string expected)
        => Assert.Equal(expected, ObjectUri.Parse(input, Azure).DuckDbUri);

    [Fact]
    public void SecretScope_Is_The_Container_Not_The_Key()
        => Assert.Equal("s3://bucket", ObjectUri.Parse("s3://bucket/a/b.parquet", S3).SecretScope);

    /// <summary>
    /// Globs are passed through untouched — DuckDB's read functions expand them, so the wildcard
    /// must survive parsing and the extension must still come from the last segment.
    /// </summary>
    [Theory]
    [InlineData("s3://bucket/*.parquet", "s3://bucket/*.parquet")]
    [InlineData("s3://bucket/dt=*/part-?.parquet", "s3://bucket/dt=*/part-?.parquet")]
    public void Globs_Survive_Parsing(string input, string expected)
    {
        var uri = ObjectUri.Parse(input, S3);

        Assert.Equal(expected, uri.DuckDbUri);
        Assert.Equal(".parquet", uri.Extension);
    }

    [Theory]
    [InlineData("s3://bucket")]          // container alone holds no bytes
    [InlineData("s3://bucket/")]         // trailing slash: no key
    [InlineData("gs://bucket/key.parquet")] // scheme not accepted by this provider
    [InlineData("/local/path.parquet")]
    [InlineData("C:\\data\\file.parquet")]
    [InlineData("csv:data.parquet")]
    public void TryParse_Rejects_Non_Locations(string input)
        => Assert.False(ObjectUri.TryParse(input, S3, out _));

    [Fact]
    public void Parse_Throws_With_The_Accepted_Schemes_Listed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ObjectUri.Parse("gs://bucket/key.parquet", S3));
        Assert.Contains("s3", ex.Message);
    }
}
