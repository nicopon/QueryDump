using System;
using System.Collections.Generic;
using DtPipe.Cli.Agent;
using Xunit;

namespace DtPipe.Tests.Unit.Cli;

public class ConversationWindowManagerTests
{
    [Fact]
    public void Compact_UnderThreshold_ReturnsUnchanged()
    {
        var manager = new ConversationWindowManager(maxMessages: 5, keepSystemMessages: 1, keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", "hello"),
            new("assistant", "hi")
        };

        var result = manager.Compact(messages);

        Assert.Equal(messages.Count, result.Count);
        Assert.Equal(messages, result);
    }

    [Fact]
    public void Compact_OverThreshold_SummarizesMiddleMessages()
    {
        // Max 5 messages: system (1), recent (2), so threshold is 5.
        // We will pass 6 messages.
        var manager = new ConversationWindowManager(maxMessages: 5, keepSystemMessages: 1, keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new("system", "sys-content"),
            new("user", "user-1"),
            new("assistant", "assistant-1", ToolCalls: new List<ToolCall> { new ToolCall("1", "tool-a", default) }),
            new("tool", "tool-result-1", ToolCallId: "1"),
            new("user", "user-2"),
            new("assistant", "assistant-2")
        };

        var result = manager.Compact(messages);

        // Should be: System (1) + Compacted Middle Summary (1) + Recent (2) = 4 messages
        Assert.Equal(4, result.Count);
        Assert.Equal("system", result[0].Role);
        Assert.Equal("sys-content", result[0].Content);

        Assert.Equal("assistant", result[1].Role);
        Assert.Contains("Context compacted", result[1].Content);
        Assert.Contains("tool-a", result[1].Content); // must mention compacted tools

        Assert.Equal("user", result[2].Role);
        Assert.Equal("user-2", result[2].Content);

        Assert.Equal("assistant", result[3].Role);
        Assert.Equal("assistant-2", result[3].Content);
    }
}
