using System;
using System.Collections.Generic;
using DtPipe.Adapters.Common;

namespace DtPipe.Adapters.ObjectStorage;

/// <summary>
/// Scheme ownership for the object-storage providers.
///
/// These are the only providers allowed to claim a "scheme://" connection string. Extension-based
/// file providers must keep refusing them (see <see cref="ConnectionUri.HasRemoteScheme"/>):
/// claiming "s3://bucket/x.parquet" as a local path silently wrote a directory named "s3:".
/// A location is claimed only when its format is also recognised, so an unsupported extension
/// surfaces as "no provider" rather than as a DuckDB error deep into the run.
/// </summary>
public static class ObjectStorageMetadata
{
    public const string S3ComponentName = "s3";
    public const string AzureComponentName = "azure";

    public static readonly IReadOnlySet<string> S3Schemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s3", "s3a" };

    public static readonly IReadOnlySet<string> AzureSchemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "azure", "az" };

    public static bool CanHandleS3(string connectionString) => CanHandle(connectionString, S3Schemes);

    public static bool CanHandleAzure(string connectionString) => CanHandle(connectionString, AzureSchemes);

    private static bool CanHandle(string connectionString, IReadOnlySet<string> schemes)
        => ObjectUri.TryParse(connectionString, schemes, out var uri)
           && uri is not null
           && ObjectFormatMap.IsSupported(uri.Extension);
}
