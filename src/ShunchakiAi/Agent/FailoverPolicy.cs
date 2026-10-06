using ShunchakiAi.Api;

namespace ShunchakiAi.Agent;

public enum FailureScope
{
    /// <summary>Not model-specific (bad key, no credit, malformed request): switching won't help.</summary>
    Fatal,

    /// <summary>The model is temporarily unavailable; retry it after the cooldown.</summary>
    Temporary,

    /// <summary>The model cannot serve this conversation at all (not enabled, context too small).</summary>
    Conversation,
}

public sealed record Failure(FailureScope Scope, TimeSpan Cooldown, string Reason);

/// <summary>Decides whether an API failure should move the work to another model.</summary>
public static class FailoverPolicy
{
    public static Failure Classify(AnthropicApiException ex)
    {
        var message = ex.Message;
        var cooldown = ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? retryAfter
            : TimeSpan.FromSeconds(60);

        if (message.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
            || message.Contains("billing", StringComparison.OrdinalIgnoreCase))
        {
            return new Failure(FailureScope.Fatal, TimeSpan.Zero,
                "The account has run out of API credit; every model uses the same balance. Top up in the Anthropic Console.");
        }

        return ex.StatusCode switch
        {
            429 => new Failure(FailureScope.Temporary, cooldown, "rate limit / token quota reached"),
            529 => new Failure(FailureScope.Temporary, TimeSpan.FromSeconds(30), "model overloaded"),
            >= 500 => new Failure(FailureScope.Temporary, TimeSpan.FromSeconds(30), $"server error {ex.StatusCode}"),
            404 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "model not found"),
            403 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "model not available to this API key"),
            413 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "conversation too large for this model"),
            400 when IsContextOverflow(message) =>
                new Failure(FailureScope.Conversation, TimeSpan.Zero, "conversation exceeds this model's context window"),
            400 when message.Contains("model", StringComparison.OrdinalIgnoreCase)
                     && message.Contains("not supported", StringComparison.OrdinalIgnoreCase) =>
                new Failure(FailureScope.Conversation, TimeSpan.Zero, "request not supported by this model"),
            _ => new Failure(FailureScope.Fatal, TimeSpan.Zero, message),
        };
    }

    public static Failure Network(Exception ex) =>
        new(FailureScope.Temporary, TimeSpan.FromSeconds(20), $"network error: {ex.Message}");

    private static bool IsContextOverflow(string message) =>
        message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
        || message.Contains("context window", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase);
}
