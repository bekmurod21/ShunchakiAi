using System.Text.Json.Nodes;

namespace ShunchakiAi.Api;

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Input);

public readonly record struct TokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens);
}

/// <summary>
/// One response from <c>POST /v1/messages</c>. <see cref="Content"/> is kept as the raw
/// JSON array so it can be appended to the history byte-for-byte (thinking blocks must be
/// echoed back unchanged).
/// </summary>
public sealed record ModelResponse(string StopReason, JsonArray Content, string? RefusalDetail, TokenUsage Usage)
{
    public IEnumerable<ToolCall> ToolCalls =>
        Content.OfType<JsonObject>()
            .Where(block => BlockType(block) == "tool_use")
            .Select(block => new ToolCall(
                block["id"]?.GetValue<string>() ?? string.Empty,
                block["name"]?.GetValue<string>() ?? string.Empty,
                block["input"] as JsonObject ?? []));

    public static string? BlockType(JsonObject block) => block["type"]?.GetValue<string>();

    public static ModelResponse Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            throw new AnthropicApiException(0, "invalid_response", "Response body was not a JSON object.");
        }

        // Detach the content array so it can be re-parented into the conversation history.
        var content = root["content"] as JsonArray ?? [];
        root.Remove("content");

        var stopReason = root["stop_reason"]?.GetValue<string>() ?? "end_turn";

        string? refusalDetail = null;
        if (stopReason == "refusal" && root["stop_details"] is JsonObject details)
        {
            refusalDetail = details["explanation"]?.GetValue<string>() ?? details["category"]?.GetValue<string>();
        }

        var usage = root["usage"] as JsonObject;
        return new ModelResponse(stopReason, content, refusalDetail, new TokenUsage(
            ReadLong(usage, "input_tokens"),
            ReadLong(usage, "output_tokens"),
            ReadLong(usage, "cache_read_input_tokens"),
            ReadLong(usage, "cache_creation_input_tokens")));
    }

    private static long ReadLong(JsonObject? obj, string name) =>
        obj?[name] is JsonValue value && value.TryGetValue<long>(out var result) ? result : 0;
}
