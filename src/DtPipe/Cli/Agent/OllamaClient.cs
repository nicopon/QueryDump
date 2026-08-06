using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Cli.Agent;

public class OllamaClient
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public record OllamaModelInfo(string Name, long Size, DateTime ModifiedAt);

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

    public record ToolFunction(string Name, string Description, JsonElement Parameters);
    public record ToolDefinition(string Type, ToolFunction Function);

    public record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("tool_calls")] List<OllamaToolCall>? ToolCalls = null
    );

    public record OllamaToolCall(
        [property: JsonPropertyName("function")] OllamaFunctionCall Function
    );

    public record OllamaFunctionCall(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] JsonElement Arguments
    );

    public record ChatResponse(
        string Model,
        OllamaChatMessage Message,
        bool Done,
        string? Error
    );

    public async Task<ChatResponse> ChatAsync(
        string baseUrl,
        string model,
        List<OllamaChatMessage> messages,
        List<ToolDefinition> tools,
        int numCtx = 16384,
        CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/api/chat";

        var requestBody = new
        {
            model = model,
            messages = messages,
            tools = tools,
            options = new { num_ctx = numCtx },
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

                toolCalls.Add(new OllamaToolCall(new OllamaFunctionCall(fnName, fnArgs.Clone())));
            }
        }

        return new ChatResponse(model, new OllamaChatMessage(role, msgContent, null, toolCalls), true, null);
    }
}
