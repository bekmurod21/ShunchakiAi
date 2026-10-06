using System.Globalization;
using System.Text.Json;

namespace ShunchakiAi.Api;

/// <summary>A non-success response from a model provider's API (Anthropic or Gemini).</summary>
public sealed class ModelApiException(string provider, int statusCode, string errorType, string message)
    : Exception($"{provider} API error {statusCode} ({errorType}): {message}")
{
    public string Provider { get; } = provider;
    public int StatusCode { get; } = statusCode;
    public string ErrorType { get; } = errorType;

    /// <summary>Server-suggested wait before retrying, when the response carried one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Anthropic: <c>{"type":"error","error":{"type":...,"message":...}}</c>.</summary>
    public static ModelApiException FromAnthropic(int statusCode, string body, TimeSpan? retryAfter)
    {
        if (TryParse(body, out var root) && root.TryGetProperty("error", out var error))
        {
            return new ModelApiException(ModelProviders.Anthropic, statusCode,
                GetString(error, "type") ?? "unknown_error", GetString(error, "message") ?? body) { RetryAfter = retryAfter };
        }

        return Raw(ModelProviders.Anthropic, statusCode, body, retryAfter);
    }

    /// <summary>
    /// Gemini: <c>{"error":{"code":429,"message":...,"status":"RESOURCE_EXHAUSTED","details":[...]}}</c>.
    /// Quota errors carry a <c>google.rpc.RetryInfo</c> detail with <c>retryDelay</c> (e.g. "41s").
    /// </summary>
    public static ModelApiException FromGemini(int statusCode, string body, TimeSpan? retryAfter)
    {
        if (TryParse(body, out var root) && root.TryGetProperty("error", out var error))
        {
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (GetString(detail, "retryDelay") is { } delay && delay.EndsWith('s')
                        && double.TryParse(delay[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        retryAfter = TimeSpan.FromSeconds(seconds);
                    }
                }
            }

            return new ModelApiException(ModelProviders.Gemini, statusCode,
                GetString(error, "status") ?? "UNKNOWN", GetString(error, "message") ?? body) { RetryAfter = retryAfter };
        }

        return Raw(ModelProviders.Gemini, statusCode, body, retryAfter);
    }

    private static ModelApiException Raw(string provider, int statusCode, string body, TimeSpan? retryAfter)
    {
        // Not JSON (e.g. a proxy HTML page): report the start of the raw body.
        var snippet = body.Length > 500 ? body[..500] + "..." : body;
        return new ModelApiException(provider, statusCode, "http_error", snippet) { RetryAfter = retryAfter };
    }

    private static bool TryParse(string body, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
