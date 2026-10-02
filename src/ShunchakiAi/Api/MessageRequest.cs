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
    public byte[] ToUtf8Json()
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
            Messages.WriteTo(writer);

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
