using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

public class OllamaClient : ILlmClient
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public string ProviderName => "ollama";

    public record OllamaModelInfo(string Name, long Size, DateTime ModifiedAt);

    public async Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        var models = await GetAvailableModelsAsync(baseUrl, ct);
        return models.Select(m => m.Name).ToList();
    }

    public async Task<List<OllamaModelInfo>> GetAvailableModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var url = baseUrl.TrimEnd('/') + "/api/tags";
            var response = await HttpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new List<OllamaModelInfo>();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = new List<OllamaModelInfo>();

            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in modelsArray.EnumerateArray())
                {
                    var name = el.GetProperty("name").GetString() ?? "";
                    var size = el.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    var modifiedAt = el.TryGetProperty("modified_at", out var m) && DateTime.TryParse(m.GetString(), out var dt) ? dt : DateTime.MinValue;
                    if (!string.IsNullOrEmpty(name))
                    {
                        models.Add(new OllamaModelInfo(name, size, modifiedAt));
                    }
                }
            }

            return models;
        }
        catch
        {
            return new List<OllamaModelInfo>();
        }
    }

    private record ToolFunction(string Name, string Description, JsonElement Parameters);
    private record OllamaToolDefinition(string Type, ToolFunction Function);

    private record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
        [property: JsonPropertyName("tool_calls")] List<OllamaToolCall>? ToolCalls = null
    );

    private record OllamaToolCall(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("function")] OllamaFunctionCall Function
    );

    private record OllamaFunctionCall(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] JsonElement Arguments
    );

    private record ChatResponse(
        string Model,
        OllamaChatMessage Message,
        bool Done,
        string? Error
    );

    public async Task<LlmResponse> ChatAsync(
        string baseUrl,
        string model,
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        int numCtx = 16384,
        double temperature = 0.7,
        int? seed = null,
        CancellationToken ct = default)
     {
         // Map messages to OllamaChatMessage
        var ollamaMessages = messages.Select(m => new OllamaChatMessage(
            m.Role,
            m.Content,
            m.Name,
            m.ToolCallId,
            m.ToolCalls?.Select(tc => new OllamaToolCall(tc.Id, new OllamaFunctionCall(tc.Name, tc.Arguments))).ToList()
        )).ToList();

         // Map tools to OllamaToolDefinition
        var ollamaTools = tools.Select(t => new OllamaToolDefinition(
             "function",
            new ToolFunction(t.Name, t.Description, t.ParametersSchema)
        )).ToList();

        var response = await InternalChatAsync(baseUrl, model, ollamaMessages, ollamaTools, numCtx, temperature, seed, ct);

        if (response.Error != null)
        {
            return new LlmResponse(new ChatMessage("assistant", null), true, response.Error);
        }

        var assistantMsg = new ChatMessage(
            response.Message.Role,
            response.Message.Content,
            response.Message.Name,
            response.Message.ToolCalls?.Select(tc => new ToolCall(tc.Id ?? $"call_{Guid.NewGuid():N}", tc.Function.Name, tc.Function.Arguments)).ToList(),
            response.Message.ToolCallId
        );

        return new LlmResponse(assistantMsg, response.Done, null);
    }

    private async Task<ChatResponse> InternalChatAsync(
        string baseUrl,
        string model,
        List<OllamaChatMessage> messages,
        List<OllamaToolDefinition> tools,
        int numCtx,
        double temperature,
        int? seed,
        CancellationToken ct)
     {
        var url = baseUrl.TrimEnd('/') + "/api/chat";

        // temperature is always sent so a run can be made fully deterministic (temperature = 0).
        // seed is only sent when explicitly provided (null => omit, provider picks its own).
        var options = new Dictionary<string, object>
         {
            ["num_ctx"] = numCtx,
            ["temperature"] = temperature
         };
        if (seed.HasValue)
         {
            options["seed"] = seed.Value;
         }

        var requestBody = new
         {
            model = model,
            messages = messages,
            tools = tools,
            options = options,
            stream = false
         };

        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var requestJson = JsonSerializer.Serialize(requestBody, jsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await HttpClient.PostAsync(url, content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string errMessage = $"HTTP {(int)response.StatusCode}: {responseJson}";
            try
            {
                using var errDoc = JsonDocument.Parse(responseJson);
                if (errDoc.RootElement.TryGetProperty("error", out var errProp))
                {
                    errMessage = errProp.GetString() ?? errMessage;
                }
            }
            catch { }

            return new ChatResponse(model, new OllamaChatMessage("assistant", null), true, errMessage);
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
        {
            return new ChatResponse(model, new OllamaChatMessage("assistant", null), true, errorEl.GetString());
        }

        var messageEl = root.GetProperty("message");
        var role = messageEl.GetProperty("role").GetString() ?? "assistant";
        var msgContent = messageEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;

        List<OllamaToolCall>? toolCalls = null;
        if (messageEl.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
        {
            toolCalls = new List<OllamaToolCall>();
            foreach (var tc in tcArray.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var fnEl = tc.GetProperty("function");
                var fnName = fnEl.GetProperty("name").GetString() ?? "";
                var fnArgs = fnEl.GetProperty("arguments");

                // If fnArgs is a string representation of JSON, re-parse it as JsonElement
                if (fnArgs.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        using var parsedArgsDoc = JsonDocument.Parse(fnArgs.GetString()!);
                        fnArgs = parsedArgsDoc.RootElement.Clone();
                    }
                    catch { }
                }

                toolCalls.Add(new OllamaToolCall(id, new OllamaFunctionCall(fnName, fnArgs.Clone())));
            }
        }

        return new ChatResponse(role, new OllamaChatMessage(role, msgContent, null, null, toolCalls), true, null);
    }
}
