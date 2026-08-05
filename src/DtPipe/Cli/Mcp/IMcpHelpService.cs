using System.Collections.Generic;
using DtPipe.Core.Abstractions;

namespace DtPipe.Cli.Mcp;

public interface IMcpHelpService
{
    string GetGeneralHelp();
    string GetAdapterHelp(string adapterName);
    string GetTransformerHelp(string transformerName);
    string GetAnonymizationHelp();
}
