using System;
using DtPipe.Adapters.DuckDB;
using DtPipe.Adapters.ObjectStorage;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DtPipe.Adapters.S3;

public class S3WriterDescriptor : IProviderDescriptor<IDataWriter>
{
    public string ComponentName => ObjectStorageMetadata.S3ComponentName;
    public string Category => "Writer Options";
    public Type OptionsType => typeof(S3WriterOptions);
    public bool CanHandle(string connectionString) => ObjectStorageMetadata.CanHandleS3(connectionString);
    public bool SupportsStdio => false;
    public bool RequiresQuery => false;

    public IDataWriter Create(string connectionString, object options, IServiceProvider serviceProvider)
    {
        var opt = (S3WriterOptions)options;
        var binding = ObjectStorageFactory.ForS3(connectionString, opt);
        return new ObjectStorageDataWriter(
            binding,
            serviceProvider.GetRequiredService<ILogger<DuckDbDataWriter>>(),
            new DuckDbTypeConverter(),
            serviceProvider.GetService<IStringContentResolver>());
    }
}
