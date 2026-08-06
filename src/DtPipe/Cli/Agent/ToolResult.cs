using System;
using System.Text.Json;

namespace DtPipe.Cli.Agent;

public record ToolResult(string Content, bool IsError)
{
    public static ToolResult Success(string content) => new(content, false);
    public static ToolResult Error(string content) => new(content, true);

    /// <summary>
    /// Parse a JSON tool response and detect errors structurally (top-level "error" key or "success": false).
    /// </summary>
    public static ToolResult FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ToolResult("{}", false);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool isError = false;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind != JsonValueKind.Null && errProp.ValueKind != JsonValueKind.Undefined)
                {
                    isError = true;
                }
                else if (root.TryGetProperty("success", out var successProp) && successProp.ValueKind == JsonValueKind.False)
                {
                    isError = true;
                }
            }

            return new ToolResult(json, isError);
        }
        catch
        {
            return new ToolResult(json, false);
        }
    }
}
