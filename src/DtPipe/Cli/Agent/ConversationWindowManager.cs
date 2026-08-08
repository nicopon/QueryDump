using System;
using System.Collections.Generic;
using System.Linq;

namespace DtPipe.Cli.Agent;

public class ConversationWindowManager
{
    private readonly int _maxMessages;
    private readonly int _keepSystemMessages;
    private readonly int _keepRecentMessages;

    public ConversationWindowManager(
        int maxMessages = 40,
        int keepSystemMessages = 1,
        int keepRecentMessages = 10)
    {
        _maxMessages = maxMessages;
        _keepSystemMessages = keepSystemMessages;
        _keepRecentMessages = keepRecentMessages;
    }

    /// <summary>
    /// If the message count exceeds maxMessages, summarizes middle messages
    /// into a single synthetic "assistant" message, keeping system + recent messages.
    /// </summary>
    public List<ChatMessage> Compact(List<ChatMessage> messages)
    {
        if (messages.Count <= _maxMessages) return messages;

        var systemMessages = messages.Take(_keepSystemMessages).ToList();
        var recentMessages = messages.TakeLast(_keepRecentMessages).ToList();
        var middleMessages = messages
            .Skip(_keepSystemMessages)
            .Take(messages.Count - _keepSystemMessages - _keepRecentMessages)
            .ToList();

        // Summarize intermediate messages
        var toolCalls = middleMessages
            .Where(m => m.ToolCalls != null)
            .SelectMany(m => m.ToolCalls!)
            .Select(tc => tc.Name)
            .ToList();

        var summary = $"[Context compacted: {middleMessages.Count} messages summarized. " +
                      $"Tools called: {string.Join(", ", toolCalls.Distinct())}. " +
                      $"Keeping {_keepRecentMessages} most recent messages.]";

        var result = new List<ChatMessage>();
        result.AddRange(systemMessages);
        result.Add(new ChatMessage("assistant", summary));
        result.AddRange(recentMessages);
        return result;
    }
}
