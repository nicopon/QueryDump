using System;
using System.CommandLine;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;

namespace DtPipe.Cli.Commands;

public class McpCommand : Command
{
    public McpCommand(IServiceProvider serviceProvider) : base("mcp", "Start the MCP STDIO server for AI assistants")
    {
        this.SetAction(async (parseResult, ct) =>
        {
            // Set security context for MCP
            var securityContext = serviceProvider.GetService<DtPipe.Core.Security.IMcpSecurityContext>();
            if (securityContext != null)
            {
                securityContext.IsMcpSession = true;
            }

            // Resolve all hosted services and start them in registration order.
            // This avoids fragile string-based type resolution.
            var hostedServices = serviceProvider.GetServices<IHostedService>();
            Console.Error.WriteLine("[MCP] Server starting on STDIO...");

            foreach (var service in hostedServices)
            {
                await service.StartAsync(ct);
            }

            // Wait indefinitely until cancellation
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
            }
            finally
            {
                foreach (var service in hostedServices)
                {
                    try { await service.StopAsync(default); }
                    catch { /* Best effort cleanup */ }
                }
            }
        });
    }
}
