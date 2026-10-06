using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShunchakiAi.Api;

/// <summary>
/// Google Gemini API (<c>POST v1beta/models/{model}:generateContent</c>) over <see cref="HttpClient"/>.
/// The canonical history (Messages-API format) is translated to Gemini <c>contents</c> on every
/// request, and Gemini's reply is translated back, so a conversation can move between Claude and
/// Gemini models in either direction with exactly the same context.
/// </summary>
public sealed class GeminiClient : IModelProvider
{
    /// <summary>
    /// Google's documented placeholder for function calls that Gemini did not produce itself
    /// (calls made by another model, or replayed history). Gemini 3 rejects unsigned function calls.
    /// </summary>
    public const string ForeignCallSignature = "skip_thought_signature_validator";

    /// <summary>Where a Gemini thought signature is kept on a canonical content block.</summary>
    public const string SignatureField = MessageRequest.PrivateFieldPrefix + "gemini_thought_signature";

    private readonly HttpClient _http;

    public GeminiClient(Uri baseUrl, string apiKey)
    {
        _http = HttpSender.CreateClient(baseUrl);
        _http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
    }

    public string Name => ModelProviders.Gemini;

    public async Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken)
    {
        var body = await HttpSender.PostJsonAsync(
            _http,
            $"v1beta/models/{Uri.EscapeDataString(request.Model)}:generateContent",
            BuildRequest(request),
            configure: null,
            ModelApiException.FromGemini,
            maxRetries,
            cancellationToken).ConfigureAwait(false);

        return ParseResponse(body);
    }

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- request

    public static byte[] BuildRequest(MessageRequest request)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = ToContents(request.Messages),
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = ToFunctionDeclarations(request.Tools) }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = request.MaxTokens,
                ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true },
            },
        };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            body.WriteTo(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>Canonical messages → Gemini contents (user / model turns with parts).</summary>
    public static JsonArray ToContents(JsonArray messages)
    {
        var contents = new JsonArray();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject? previous = null;

        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = message["role"]?.GetValue<string>() == "assistant" ? "model" : "user";
            var parts = new JsonArray();

            foreach (var block in Blocks(message["content"]))
            {
                var part = ToPart(block, toolNames);
                if (part is not null)
                {
                    parts.Add((JsonNode)part);
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            // Gemini expects alternating turns: merge consecutive messages of the same role.
            if (previous is not null && previous["role"]?.GetValue<string>() == role && previous["parts"] is JsonArray previousParts)
            {
                foreach (var part in parts.ToList())
                {
                    parts.Remove(part);
                    previousParts.Add(part);
                }

                continue;
            }

            previous = new JsonObject { ["role"] = role, ["parts"] = parts };
            contents.Add((JsonNode)previous);
        }

        return contents;
    }

    private static JsonObject? ToPart(JsonObject block, Dictionary<string, string> toolNames)
    {
        var signature = block[SignatureField]?.GetValue<string>();
        switch (ModelResponse.BlockType(block))
        {
            case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                var textPart = new JsonObject { ["text"] = text };
                if (signature is not null)
                {
                    textPart["thoughtSignature"] = signature;
                }

                return textPart;

            case "tool_use":
                var name = block["name"]?.GetValue<string>() ?? "unknown";
                if (block["id"]?.GetValue<string>() is { } id)
                {
                    toolNames[id] = name;
                }

                return new JsonObject
                {
                    ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = block["input"]?.DeepClone() ?? new JsonObject() },
                    ["thoughtSignature"] = signature ?? ForeignCallSignature,
                };

            case "tool_result":
                var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? string.Empty;
                var isError = block["is_error"]?.GetValue<bool>() ?? false;
                return new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = toolNames.GetValueOrDefault(toolUseId, "unknown"),
                        ["response"] = new JsonObject { [isError ? "error" : "output"] = ResultText(block["content"]) },
                    },
                };

            default:
                // Claude thinking blocks are bound to Claude models; Gemini cannot use them.
                return null;
        }
    }

    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>()).Where(t => t is not null)),
        _ => string.Empty,
    };

    private static IEnumerable<JsonObject> Blocks(JsonNode? content) => content switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonValue value when value.TryGetValue<string>(out var text) => [new JsonObject { ["type"] = "text", ["text"] = text }],
        _ => [],
    };

    /// <summary>Gemini's schema dialect (OpenAPI subset) rejects <c>additionalProperties</c>.</summary>
    private static JsonArray ToFunctionDeclarations(JsonArray tools)
    {
        var declarations = new JsonArray();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var schema = tool["input_schema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" };
            StripUnsupported(schema);
            declarations.Add((JsonNode)new JsonObject
            {
                ["name"] = tool["name"]?.GetValue<string>(),
                ["description"] = tool["description"]?.GetValue<string>(),
                ["parameters"] = schema,
            });
        }

        return declarations;
    }

    private static void StripUnsupported(JsonObject schema)
    {
        schema.Remove("additionalProperties");
        schema.Remove("$schema");
        foreach (var (_, child) in schema.ToList())
        {
            if (child is JsonObject childObject)
            {
                StripUnsupported(childObject);
            }
        }
    }

    // --------------------------------------------------------------- response

    public static ModelResponse ParseResponse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            throw new ModelApiException(ModelProviders.Gemini, 0, "invalid_response", "Response body was not a JSON object.");
        }

        var usage = root["usageMetadata"] as JsonObject;
        var tokenUsage = new TokenUsage(
            Long(usage, "promptTokenCount") - Long(usage, "cachedContentTokenCount"),
            Long(usage, "candidatesTokenCount") + Long(usage, "thoughtsTokenCount"),
            Long(usage, "cachedContentTokenCount"),
            0);

        var candidate = (root["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (candidate is null)
        {
            // The prompt itself was blocked by Gemini's safety filters.
            var reason = root["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? "no candidates returned";
            return new ModelResponse("refusal", [], $"Gemini blocked the prompt ({reason})", tokenUsage);
        }

        var finishReason = candidate["finishReason"]?.GetValue<string>() ?? "STOP";
        var content = new JsonArray();
        var thoughts = new List<string>();
        string? carriedSignature = null;

        foreach (var part in (candidate["content"]?["parts"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var signature = part["thoughtSignature"]?.GetValue<string>() ?? carriedSignature;
            carriedSignature = null;

            if (part["thought"]?.GetValue<bool>() == true)
            {
                if (part["text"]?.GetValue<string>() is { Length: > 0 } thought)
                {
                    thoughts.Add(thought);
                }

                carriedSignature = signature; // keep it on the next stored part
                continue;
            }

            JsonObject? block = null;
            if (part["functionCall"] is JsonObject call)
            {
                block = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_gm" + Guid.NewGuid().ToString("N")[..22],
                    ["name"] = call["name"]?.GetValue<string>() ?? "unknown",
                    ["input"] = call["args"]?.DeepClone() as JsonObject ?? new JsonObject(),
                };
            }
            else if (part["text"]?.GetValue<string>() is { Length: > 0 } text)
            {
                block = new JsonObject { ["type"] = "text", ["text"] = text };
            }

            if (block is null)
            {
                carriedSignature = signature;
                continue;
            }

            if (signature is not null)
            {
                block[SignatureField] = signature;
            }

            content.Add((JsonNode)block);
        }

        var hasToolCalls = content.OfType<JsonObject>().Any(b => ModelResponse.BlockType(b) == "tool_use");
        var stopReason = finishReason switch
        {
            _ when hasToolCalls => "tool_use",
            "MAX_TOKENS" => "max_tokens",
            "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION" or "IMAGE_SAFETY" => "refusal",
            _ => "end_turn",
        };

        if (stopReason == "refusal")
        {
            return new ModelResponse("refusal", [], $"Gemini stopped generation ({finishReason})", tokenUsage) { Thoughts = thoughts };
        }

        if (content.Count == 0)
        {
            // Never store an empty assistant turn: every provider rejects one in the history.
            var note = finishReason == "MALFORMED_FUNCTION_CALL"
                ? "(Gemini produced a malformed tool call; please retry the step.)"
                : "(no response)";
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = note });
        }

        return new ModelResponse(stopReason, content, null, tokenUsage) { Thoughts = thoughts };
    }

    private static long Long(JsonObject? obj, string name) =>
        obj?[name] is JsonValue value && value.TryGetValue<long>(out var result) ? result : 0;
}
