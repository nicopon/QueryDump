using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

public interface ILlmClient
{
    /// <summary>Nom du provider (ex: "ollama", "openai")</summary>
    string ProviderName { get; }

    /// <summary>Liste les modèles disponibles sur ce backend.</summary>
    Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct = default);

    /// <summary>Envoie un chat completion avec function calling.</summary>
    Task<LlmResponse> ChatAsync(
        string baseUrl,
        string model,
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        int maxTokens = 16384,
        CancellationToken ct = default);
}
