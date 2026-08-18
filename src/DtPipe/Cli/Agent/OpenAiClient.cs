using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace DtPipe.Cli.Agent;

// The OpenAI .NET SDK flags ChatCompletionOptions.Seed with OPENAI001 ("for evaluation
// purposes only"). We use it deliberately to make agent runs deterministic and replicable
// (F3), so the diagnostic is suppressed for this file.
#pragma warning disable OPENAI001

public class OpenAiClient : ILlmClient
{
    private readonly string _apiKey;

    public string ProviderName => "openai";

    public OpenAiClient(string? apiKey = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("DTPIPE_LLM_API_KEY") ?? "";
    }

    public async Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var url = baseUrl.TrimEnd('/') + "/v1/models";
            if (!string.IsNullOrEmpty(_apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await client.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new List<string>();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var list = new List<string>();

            if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in dataArray.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        list.Add(idEl.GetString()!);
                    }
                }
            }

            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<LlmResponse> ChatAsync(
        string baseUrl,
        string model,
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        int maxTokens = 16384,
        double temperature = 0.7,
        int? seed = null,
        CancellationToken ct = default)
      {
        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(baseUrl))
            {
                clientOptions.Endpoint = new Uri(baseUrl.TrimEnd('/') + "/v1");
            }

            var chatClient = new ChatClient(model, new ApiKeyCredential(_apiKey), clientOptions);

            var sdkMessages = new List<OpenAI.Chat.ChatMessage>();
            foreach (var msg in messages)
            {
                if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    sdkMessages.Add(new SystemChatMessage(msg.Content));
                }
                else if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    sdkMessages.Add(new UserChatMessage(msg.Content));
                }
                else if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        var assistantToolCalls = msg.ToolCalls.Select(tc => ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Name, BinaryData.FromString(tc.Arguments.GetRawText()))).ToList();
                        var assistantMsg = new AssistantChatMessage(assistantToolCalls);
                        if (!string.IsNullOrEmpty(msg.Content))
                        {
                            assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(msg.Content));
                        }
                        sdkMessages.Add(assistantMsg);
                    }
                    else
                    {
                        sdkMessages.Add(new AssistantChatMessage(msg.Content));
                    }
                }
                else if (msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    sdkMessages.Add(new ToolChatMessage(msg.ToolCallId, msg.Content));
                }
            }

            var chatTools = tools.Select(t => ChatTool.CreateFunctionTool(
                t.Name,
                t.Description,
                BinaryData.FromString(t.ParametersSchema.GetRawText())
            )).ToList();

            var options = new ChatCompletionOptions
             {
                 Temperature = (float)temperature
             };
            if (seed.HasValue)
             {
                 options.Seed = seed.Value;
             }

            foreach (var tool in chatTools)
             {
                options.Tools.Add(tool);
             }

            ClientResult<ChatCompletion> result = await chatClient.CompleteChatAsync(sdkMessages, options, ct);
            ChatCompletion completion = result.Value;

            string? content = null;
            if (completion.Content != null && completion.Content.Count > 0)
            {
                content = string.Join(Environment.NewLine, completion.Content.Select(p => p.Text));
            }

            List<ToolCall>? responseToolCalls = null;
            if (completion.ToolCalls != null && completion.ToolCalls.Count > 0)
            {
                responseToolCalls = new List<ToolCall>();
                foreach (var tc in completion.ToolCalls)
                {
                    JsonElement jsonArgs;
                    try
                    {
                        using var parsedDoc = JsonDocument.Parse(tc.FunctionArguments.ToString());
                        jsonArgs = parsedDoc.RootElement.Clone();
                    }
                    catch
                    {
                        jsonArgs = default;
                    }

                    responseToolCalls.Add(new ToolCall(tc.Id, tc.FunctionName, jsonArgs));
                }
            }

            var responseMessage = new ChatMessage(
                "assistant",
                content,
                null,
                responseToolCalls
            );

            return new LlmResponse(responseMessage, true, null);
        }
        catch (Exception ex)
        {
            return new LlmResponse(new ChatMessage("assistant", null), true, ex.Message);
        }
    }
}
