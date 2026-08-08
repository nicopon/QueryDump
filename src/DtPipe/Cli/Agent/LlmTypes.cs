using System.Collections.Generic;
using System.Text.Json;

namespace DtPipe.Cli.Agent;

// Message in the conversation (role: system, user, assistant, tool)
public record ChatMessage(
    string Role,
    string? Content,
    string? Name = null, // tool name (if Role == "tool")
    List<ToolCall>? ToolCalls = null,
    string? ToolCallId = null // tool call ID (required if Role == "tool" for OpenAI)
);

// Tool call in the LLM response
public record ToolCall(string Id, string Name, JsonElement Arguments);

// Tool definition for the LLM
public record ToolDefinition(string Name, string Description, JsonElement ParametersSchema);

// LLM response
public record LlmResponse(
    ChatMessage Message,
    bool Done,
    string? Error
);
