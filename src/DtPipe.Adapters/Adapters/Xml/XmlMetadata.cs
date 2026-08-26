namespace DtPipe.Adapters.Xml;

internal static class XmlMetadata
{
    public const string ComponentName = "xml";
    public static bool CanHandle(string connectionString) =>
        connectionString.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        !DtPipe.Adapters.Common.ConnectionUri.HasRemoteScheme(connectionString);
    public const bool SupportsStdio = true;
}
