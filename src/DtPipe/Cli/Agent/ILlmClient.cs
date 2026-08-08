using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

public interface ILlmClient
{
    /// <summary>Provider name (e.g., "ollama", "openai")</summary>
    string ProviderName { get; }

    /// <summary>Lists available models on this backend.</summary>
    Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>Sends a chat completion request with support for tool calls.</summary>
    Task<LlmResponse> ChatAsync(
        string baseUrl,
        string model,
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        int maxTokens = 16384,
        CancellationToken ct = default);
}
