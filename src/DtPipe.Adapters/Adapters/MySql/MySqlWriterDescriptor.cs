using DtPipe.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DtPipe.Adapters.MySql;

public class MySqlWriterDescriptor : IProviderDescriptor<IDataWriter>
{
    public string ComponentName => MySqlMetadata.ComponentName;
    public string Category => "Writer Options";
    public Type OptionsType => typeof(MySqlWriterOptions);
    public bool CanHandle(string connectionString) => MySqlMetadata.CanHandle(connectionString);
    public bool SupportsStdio => MySqlMetadata.SupportsStdio;
    public bool RequiresQuery => true;

    public IDataWriter Create(string connectionString, object options, IServiceProvider serviceProvider)
    {
        var opt = (MySqlWriterOptions)options;
        return new MySqlDataWriter(connectionString, opt, serviceProvider.GetRequiredService<ILogger<MySqlDataWriter>>(), new MySqlTypeConverter());
    }
}
