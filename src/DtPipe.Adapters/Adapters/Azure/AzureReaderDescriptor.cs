using System;
using DtPipe.Adapters.ObjectStorage;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Expressions;
using DtPipe.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DtPipe.Adapters.Azure;

public class AzureReaderDescriptor : IProviderDescriptor<IStreamReader>
{
    public string ComponentName => ObjectStorageMetadata.AzureComponentName;
    public string Category => "Reader Options";
    public Type OptionsType => typeof(AzureReaderOptions);
    public bool CanHandle(string connectionString) => ObjectStorageMetadata.CanHandleAzure(connectionString);
    public bool SupportsStdio => false;
    public bool RequiresQuery => false;
    public bool YieldsColumnarOutput => true;

    public IStreamReader Create(string connectionString, object options, IServiceProvider serviceProvider)
    {
        var opt = (AzureReaderOptions)options;
        var binding = ObjectStorageFactory.ForAzure(connectionString, opt);
        return new ObjectStorageStreamReader(
            binding,
            serviceProvider.GetService<ILogger<ObjectStorageStreamReader>>(),
            serviceProvider.GetService<IStringContentResolver>(),
            serviceProvider.GetService<IMcpSecurityContext>());
    }
}
