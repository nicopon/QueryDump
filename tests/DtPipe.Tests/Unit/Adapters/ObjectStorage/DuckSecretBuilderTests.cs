using System;
using System.Collections.Generic;
using DtPipe.Adapters.Common;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.ObjectStorage;

public class DuckSecretBuilderTests
{
    private static readonly IReadOnlySet<string> S3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s3" };
    private static readonly IReadOnlySet<string> Azure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azure" };

    private static ObjectUri S3Uri(string uri = "s3://bucket/key.parquet") => ObjectUri.Parse(uri, S3);
    private static ObjectUri AzureUri(string uri = "azure://container/blob.parquet") => ObjectUri.Parse(uri, Azure);

    [Fact]
    public void S3_With_Explicit_Keys_Emits_Them()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), null, "eu-west-1", "AKIA", "shhh", null, null);

        Assert.Contains("TYPE S3", secret.Sql);
        Assert.Contains("KEY_ID 'AKIA'", secret.Sql);
        Assert.Contains("SECRET 'shhh'", secret.Sql);
        Assert.Contains("REGION 'eu-west-1'", secret.Sql);
    }

    /// <summary>
    /// Without an explicit key pair the ambient chain must be used: failing instead would break
    /// every CI runner and EC2 instance that relies on env vars or an instance profile.
    /// </summary>
    [Fact]
    public void S3_Without_Keys_Falls_Back_To_The_Credential_Chain()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), null, "eu-west-1", null, null, null, null);

        Assert.Contains("PROVIDER credential_chain", secret.Sql);
        Assert.DoesNotContain("KEY_ID", secret.Sql);
    }

    /// <summary>
    /// DuckDB's ENDPOINT wants a bare host:port. Passing the scheme through produced a confusing
    /// connection failure, and S3-compatible endpoints need path-style addressing.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:9000", "ENDPOINT '127.0.0.1:9000'", "USE_SSL false")]
    [InlineData("https://s3.example.com", "ENDPOINT 's3.example.com'", "USE_SSL true")]
    public void S3_Endpoint_Scheme_Becomes_UseSsl(string endpoint, string expectedEndpoint, string expectedSsl)
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), endpoint, null, "k", "s", null, null);

        Assert.Contains(expectedEndpoint, secret.Sql);
        Assert.Contains(expectedSsl, secret.Sql);
        Assert.Contains("URL_STYLE 'path'", secret.Sql);
    }

    [Fact]
    public void S3_Explicit_UrlStyle_Wins_Over_The_Endpoint_Default()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), "http://minio:9000", null, "k", "s", null, "vhost");

        Assert.Contains("URL_STYLE 'vhost'", secret.Sql);
    }

    [Fact]
    public void Bare_Endpoint_Leaves_UseSsl_To_DuckDB()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), "minio:9000", null, "k", "s", null, null);

        Assert.Contains("ENDPOINT 'minio:9000'", secret.Sql);
        Assert.DoesNotContain("USE_SSL", secret.Sql);
    }

    /// <summary>
    /// Scoping is what lets one pipeline read from one bucket and write to another under
    /// different credentials without either secret servicing the other's bucket.
    /// </summary>
    [Fact]
    public void Secrets_Are_Scoped_To_Their_Container()
    {
        var read = DuckSecretBuilder.BuildS3(S3Uri("s3://in/a.parquet"), null, null, "k1", "s1", null, null);
        var write = DuckSecretBuilder.BuildS3(S3Uri("s3://out/b.parquet"), null, null, "k2", "s2", null, null);

        Assert.Contains("SCOPE 's3://in'", read.Sql);
        Assert.Contains("SCOPE 's3://out'", write.Sql);
        Assert.NotEqual(SecretName(read.Sql), SecretName(write.Sql));
    }

    [Fact]
    public void Same_Container_Yields_A_Stable_Secret_Name()
    {
        var a = DuckSecretBuilder.BuildS3(S3Uri("s3://b/one.parquet"), null, null, "k", "s", null, null);
        var b = DuckSecretBuilder.BuildS3(S3Uri("s3://b/two.parquet"), null, null, "k", "s", null, null);

        Assert.Equal(SecretName(a.Sql), SecretName(b.Sql));
    }

    [Fact]
    public void Quotes_In_Values_Are_Escaped()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), null, null, "ke'y", "sec'ret", null, null);

        Assert.Contains("KEY_ID 'ke''y'", secret.Sql);
        Assert.Contains("SECRET 'sec''ret'", secret.Sql);
    }

    /// <summary>
    /// The statement necessarily carries literal credentials, so nothing user-visible may echo
    /// it back unmasked — a DuckDB error can quote the failing statement.
    /// </summary>
    [Fact]
    public void Redact_Masks_Every_Sensitive_Value()
    {
        var secret = DuckSecretBuilder.BuildS3(S3Uri(), null, null, "AKIAEXAMPLE", "TOPSECRET", "TOKEN123", null);

        var masked = secret.Redact("boom near KEY_ID 'AKIAEXAMPLE' SECRET 'TOPSECRET' SESSION_TOKEN 'TOKEN123'");

        Assert.DoesNotContain("AKIAEXAMPLE", masked);
        Assert.DoesNotContain("TOPSECRET", masked);
        Assert.DoesNotContain("TOKEN123", masked);
        Assert.Contains("***", masked);
    }

    [Fact]
    public void Azure_Connection_String_Is_Passed_Through_And_Redacted()
    {
        var conn = "DefaultEndpointsProtocol=http;AccountName=dev;AccountKey=SECRETKEY==;";
        var secret = DuckSecretBuilder.BuildAzure(AzureUri(), conn, null, null, null, null);

        Assert.Contains("TYPE AZURE", secret.Sql);
        Assert.Contains("SCOPE 'azure://container'", secret.Sql);
        Assert.DoesNotContain("SECRETKEY", secret.Redact(secret.Sql));
    }

    [Fact]
    public void Azure_Account_Key_Builds_A_Connection_String()
    {
        var secret = DuckSecretBuilder.BuildAzure(AzureUri(), null, "acct", "KEY==", null, "http://127.0.0.1:10000/acct");

        Assert.Contains("AccountName=acct", secret.Sql);
        Assert.Contains("BlobEndpoint=http://127.0.0.1:10000/acct", secret.Sql);
        Assert.Contains("DefaultEndpointsProtocol=http", secret.Sql);
    }

    [Fact]
    public void Azure_Without_Credentials_Falls_Back_To_The_Credential_Chain()
    {
        var secret = DuckSecretBuilder.BuildAzure(AzureUri(), null, "acct", null, null, null);

        Assert.Contains("PROVIDER credential_chain", secret.Sql);
    }

    private static string SecretName(string sql)
        => sql.Split(' ')[4];
}
