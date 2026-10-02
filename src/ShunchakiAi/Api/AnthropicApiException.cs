using System.Text.Json;

namespace ShunchakiAi.Api;

/// <summary>A non-success response from the Anthropic API.</summary>
public sealed class AnthropicApiException(int statusCode, string errorType, string message)
    : Exception($"API error {statusCode} ({errorType}): {message}")
{
    public int StatusCode { get; } = statusCode;
    public string ErrorType { get; } = errorType;

    public static AnthropicApiException FromResponse(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new AnthropicApiException(statusCode, type ?? "unknown_error", message ?? body);
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy HTML page) - fall through and report the raw body.
        }

        var snippet = body.Length > 500 ? body[..500] + "..." : body;
        return new AnthropicApiException(statusCode, "http_error", snippet);
    }
}
