using System;
using DtPipe.Adapters.DuckDB;
using DtPipe.Adapters.ObjectStorage;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DtPipe.Adapters.Azure;

public class AzureWriterDescriptor : IProviderDescriptor<IDataWriter>
{
    public string ComponentName => ObjectStorageMetadata.AzureComponentName;
    public string Category => "Writer Options";
    public Type OptionsType => typeof(AzureWriterOptions);
    public bool CanHandle(string connectionString) => ObjectStorageMetadata.CanHandleAzure(connectionString);
    public bool SupportsStdio => false;
    public bool RequiresQuery => false;

    public IDataWriter Create(string connectionString, object options, IServiceProvider serviceProvider)
    {
        var opt = (AzureWriterOptions)options;
        var binding = ObjectStorageFactory.ForAzure(connectionString, opt);
        return new ObjectStorageDataWriter(
            binding,
            serviceProvider.GetRequiredService<ILogger<DuckDbDataWriter>>(),
            new DuckDbTypeConverter(),
            serviceProvider.GetService<IStringContentResolver>());
    }
}
