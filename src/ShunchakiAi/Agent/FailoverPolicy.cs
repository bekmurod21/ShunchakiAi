using ShunchakiAi.Api;

namespace ShunchakiAi.Agent;

public enum FailureScope
{
    /// <summary>The model is temporarily unavailable; retry it after the cooldown.</summary>
    Temporary,

    /// <summary>The model cannot serve this conversation (not enabled, context too small, rejected request).</summary>
    Conversation,

    /// <summary>
    /// The whole provider is unusable (invalid key, no credit): every model of that provider is
    /// excluded, and the work continues on the other provider's models.
    /// </summary>
    Provider,
}

public sealed record Failure(FailureScope Scope, TimeSpan Cooldown, string Reason);

/// <summary>Decides how an API failure moves the work to another model.</summary>
public static class FailoverPolicy
{
    public static Failure Classify(ModelApiException ex)
    {
        var message = ex.Message;
        var cooldown = ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? retryAfter
            : TimeSpan.FromSeconds(60);

        if (Contains(message, "credit balance") || Contains(message, "billing"))
        {
            return new Failure(FailureScope.Provider, TimeSpan.Zero,
                $"{ex.Provider} account has run out of credit (affects all {ex.Provider} models)");
        }

        if (ex.StatusCode == 401 || Contains(message, "API key not valid") || Contains(message, "API_KEY_INVALID")
            || ex.ErrorType == "authentication_error")
        {
            return new Failure(FailureScope.Provider, TimeSpan.Zero, $"{ex.Provider} API key is invalid");
        }

        return ex.StatusCode switch
        {
            429 => new Failure(FailureScope.Temporary, cooldown, "rate limit / token quota reached"),
            529 or 503 => new Failure(FailureScope.Temporary, TimeSpan.FromSeconds(30), "model overloaded"),
            >= 500 => new Failure(FailureScope.Temporary, TimeSpan.FromSeconds(30), $"server error {ex.StatusCode}"),
            404 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "model not found"),
            403 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "model not available to this API key"),
            413 => new Failure(FailureScope.Conversation, TimeSpan.Zero, "conversation too large for this model"),
            400 when IsContextOverflow(message) =>
                new Failure(FailureScope.Conversation, TimeSpan.Zero, "conversation exceeds this model's context window"),
            // Any other rejected request may be specific to this model or provider: try the next one.
            _ => new Failure(FailureScope.Conversation, TimeSpan.Zero, $"request rejected: {Shorten(message)}"),
        };
    }

    public static Failure Network(Exception ex) =>
        new(FailureScope.Temporary, TimeSpan.FromSeconds(20), $"network error: {ex.Message}");

    private static bool IsContextOverflow(string message) =>
        Contains(message, "prompt is too long")
        || Contains(message, "context window")
        || Contains(message, "too many tokens")
        || Contains(message, "exceeds the maximum number of tokens");

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
