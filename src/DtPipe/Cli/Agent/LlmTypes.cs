using System.Collections.Generic;
using System.Text.Json;

namespace DtPipe.Cli.Agent;

// Message dans la conversation (rôle : system, user, assistant, tool)
public record ChatMessage(
    string Role,
    string? Content,
    string? Name = null, // nom du tool (si Role == "tool")
    List<ToolCall>? ToolCalls = null,
    string? ToolCallId = null // ID du tool call (requis si Role == "tool" pour OpenAI)
);

// Appel d'outil dans la réponse du LLM
public record ToolCall(string Id, string Name, JsonElement Arguments);

// Définition d'un outil pour le LLM
public record ToolDefinition(string Name, string Description, JsonElement ParametersSchema);

// Réponse du LLM
public record LlmResponse(
    ChatMessage Message,
    bool Done,
    string? Error
);
