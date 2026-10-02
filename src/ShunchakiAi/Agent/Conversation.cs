using System.Text.Json.Nodes;
using ShunchakiAi.Api;
using ShunchakiAi.Tools;

namespace ShunchakiAi.Agent;

/// <summary>
/// Append-only message history in Messages API wire format. Assistant content is stored
/// exactly as received, so thinking blocks are replayed unchanged on the next request.
/// </summary>
public sealed class Conversation
{
    private readonly JsonArray _messages = [];

    public JsonArray Messages => _messages;

    public int Count => _messages.Count;

    public void AddUserText(string text)
    {
        var block = new JsonObject { ["type"] = "text", ["text"] = text };

        // After an interrupted tool loop the history can end on a user turn (tool results);
        // fold the new text into it rather than sending two consecutive user messages.
        if (_messages.Count > 0 && _messages[^1] is JsonObject last && Role(last) == "user"
            && last["content"] is JsonArray content)
        {
            content.Add((JsonNode)block);
            return;
        }

        _messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
    }

    public void AddAssistant(JsonArray content) =>
        _messages.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = content });

    /// <summary>All results for one assistant turn go back in a single user message.</summary>
    public void AddToolResults(IEnumerable<(string ToolUseId, ToolResult Result)> results)
    {
        var content = new JsonArray();
        foreach (var (id, result) in results)
        {
            var block = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = result.Content.Length == 0 ? "(no output)" : result.Content,
            };
            if (result.IsError)
            {
                block["is_error"] = true;
            }

            content.Add((JsonNode)block);
        }

        _messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = content });
    }

    /// <summary>
    /// If the last assistant turn requested tools that never ran (cancellation, error),
    /// answer them with error results so the history stays valid for the next request.
    /// </summary>
    public void CloseDanglingToolCalls(string reason)
    {
        if (_messages.Count == 0 || _messages[^1] is not JsonObject last || Role(last) != "assistant"
            || last["content"] is not JsonArray content)
        {
            return;
        }

        var pending = content.OfType<JsonObject>()
            .Where(b => ModelResponse.BlockType(b) == "tool_use")
            .Select(b => (b["id"]?.GetValue<string>() ?? string.Empty, ToolResult.Error(reason)))
            .ToList();

        if (pending.Count > 0)
        {
            AddToolResults(pending);
        }
    }

    /// <summary>Drops every message after <paramref name="count"/> (undo a failed turn).</summary>
    public void TruncateTo(int count)
    {
        while (_messages.Count > count)
        {
            _messages.RemoveAt(_messages.Count - 1);
        }
    }

    public void Clear() => _messages.Clear();

    private static string? Role(JsonObject message) => message["role"]?.GetValue<string>();
}
