using DtPipe.Adapters.Common;
using DtPipe.Adapters.Azure;
using DtPipe.Adapters.S3;

namespace DtPipe.Adapters.ObjectStorage;

/// <summary>Builds the DuckDB-backed binding for a claimed object-storage location.</summary>
internal static class ObjectStorageFactory
{
    public static ObjectStorageBinding ForS3(string connectionString, S3ConnectionOptions options)
    {
        var uri = ObjectUri.Parse(connectionString, ObjectStorageMetadata.S3Schemes);
        return new ObjectStorageBinding
        {
            Uri = uri,
            Format = ObjectFormatMap.Resolve(uri),
            SchemeExtension = "httpfs",
            Secret = DuckSecretBuilder.BuildS3(
                uri,
                options.Endpoint,
                options.Region,
                options.AccessKey,
                options.SecretKey,
                options.SessionToken,
                options.UrlStyle),
        };
    }

    public static ObjectStorageBinding ForAzure(string connectionString, AzureConnectionOptions options)
    {
        var uri = ObjectUri.Parse(connectionString, ObjectStorageMetadata.AzureSchemes);
        return new ObjectStorageBinding
        {
            Uri = uri,
            Format = ObjectFormatMap.Resolve(uri),
            SchemeExtension = "azure",
            Secret = DuckSecretBuilder.BuildAzure(
                uri,
                options.ConnectionString,
                options.AccountName,
                options.AccountKey,
                options.SasToken,
                options.Endpoint),
        };
    }
}
