using System;
using DtPipe.Adapters.ObjectStorage;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Expressions;
using DtPipe.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DtPipe.Adapters.S3;

public class S3ReaderDescriptor : IProviderDescriptor<IStreamReader>
{
    public string ComponentName => ObjectStorageMetadata.S3ComponentName;
    public string Category => "Reader Options";
    public Type OptionsType => typeof(S3ReaderOptions);
    public bool CanHandle(string connectionString) => ObjectStorageMetadata.CanHandleS3(connectionString);
    public bool SupportsStdio => false;
    public bool RequiresQuery => false;
    public bool YieldsColumnarOutput => true;

    public IStreamReader Create(string connectionString, object options, IServiceProvider serviceProvider)
    {
        var opt = (S3ReaderOptions)options;
        var binding = ObjectStorageFactory.ForS3(connectionString, opt);
        return new ObjectStorageStreamReader(
            binding,
            serviceProvider.GetService<ILogger<ObjectStorageStreamReader>>(),
            serviceProvider.GetService<IStringContentResolver>(),
            serviceProvider.GetService<IMcpSecurityContext>());
    }
}
