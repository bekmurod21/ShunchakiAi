using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShunchakiAi.Api;

/// <summary>
/// The inputs for one Messages API call. The conversation and tool definitions are
/// written straight into the request stream, so the history is never cloned.
/// </summary>
public sealed record MessageRequest(
    string Model,
    int MaxTokens,
    string SystemPrompt,
    JsonArray Tools,
    JsonArray Messages,
    string? Effort,
    bool AdaptiveThinking,
    bool ServerFallback)
{
    /// <summary>
    /// Prefix for provider-private metadata kept on content blocks in the canonical history
    /// (e.g. Gemini thought signatures). Never sent to the Anthropic API.
    /// </summary>
    public const string PrivateFieldPrefix = "_";

    public byte[] ToAnthropicJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", Model);
            writer.WriteNumber("max_tokens", MaxTokens);

            // Top-level automatic prompt caching: the stable prefix (tools + system + history)
            // is re-read from cache on every agent-loop iteration.
            writer.WriteStartObject("cache_control");
            writer.WriteString("type", "ephemeral");
            writer.WriteEndObject();

            writer.WriteStartArray("system");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", SystemPrompt);
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (AdaptiveThinking)
            {
                writer.WriteStartObject("thinking");
                writer.WriteString("type", "adaptive");
                writer.WriteString("display", "summarized");
                writer.WriteEndObject();

                if (Effort is not null)
                {
                    writer.WriteStartObject("output_config");
                    writer.WriteString("effort", Effort);
                    writer.WriteEndObject();
                }
            }

            if (ServerFallback)
            {
                // Re-runs a classifier-declined request on Anthropic's recommended fallback model.
                writer.WriteString("fallbacks", "default");
            }

            writer.WritePropertyName("tools");
            Tools.WriteTo(writer);

            writer.WritePropertyName("messages");
            WriteMessagesWithoutPrivateFields(writer);

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private void WriteMessagesWithoutPrivateFields(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var message in Messages)
        {
            if (message is not JsonObject obj || obj["content"] is not JsonArray content)
            {
                message?.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (var (name, value) in obj)
            {
                writer.WritePropertyName(name);
                if (name != "content")
                {
                    WriteValue(writer, value);
                    continue;
                }

                writer.WriteStartArray();
                foreach (var block in content)
                {
                    if (block is not JsonObject blockObject)
                    {
                        WriteValue(writer, block);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (var (field, fieldValue) in blockObject)
                    {
                        if (!field.StartsWith(PrivateFieldPrefix, StringComparison.Ordinal))
                        {
                            writer.WritePropertyName(field);
                            WriteValue(writer, fieldValue);
                        }
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonNode? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}
