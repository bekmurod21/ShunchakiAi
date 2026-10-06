namespace ShunchakiAi.Api;

public enum KeyCheckResult
{
    Valid,
    Invalid,

    /// <summary>The key could not be verified (network problem, unexpected response).</summary>
    Unknown,
}

public sealed record KeyCheck(KeyCheckResult Result, string Message);

/// <summary>
/// One LLM backend. Every provider takes the same <see cref="MessageRequest"/> (history in the
/// canonical Messages-API format) and returns a <see cref="ModelResponse"/> in that same format,
/// so the conversation never depends on which provider produced a turn.
/// </summary>
public interface IModelProvider : IDisposable
{
    string Name { get; }

    Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken);

    /// <summary>Verifies the key with a free metadata call (lists models; no tokens are spent).</summary>
    Task<KeyCheck> CheckKeyAsync(CancellationToken cancellationToken);
}

public static class ModelProviders
{
    public const string Anthropic = "Anthropic";
    public const string Gemini = "Gemini";

    /// <summary>Claude Code CLI, logged in with a Claude Pro/Max subscription (model id <c>claude-code[:model]</c>).</summary>
    public const string ClaudeCode = "ClaudeCode";

    /// <summary>Gemini CLI, logged in with a Google account (model id <c>gemini-cli[:model]</c>).</summary>
    public const string GeminiCli = "GeminiCli";

    public static string ProviderOf(string model)
    {
        if (model.StartsWith("claude-code", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeCode;
        }

        if (model.StartsWith("gemini-cli", StringComparison.OrdinalIgnoreCase))
        {
            return GeminiCli;
        }

        return model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? Gemini : Anthropic;
    }

    public static bool IsSubscription(string provider) => provider is ClaudeCode or GeminiCli;

    public static string DisplayName(string provider) => provider switch
    {
        Gemini => "Gemini (Google API key)",
        ClaudeCode => "Claude Code (Pro/Max subscription)",
        GeminiCli => "Gemini CLI (Google account)",
        _ => "Claude (Anthropic API key)",
    };
}

/// <summary>
/// Routes each model id to the provider that serves it. Providers can be added, replaced or
/// removed while the program runs (keys entered with /login), so the model chain grows and
/// shrinks without a restart.
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    private readonly Dictionary<string, IModelProvider> _providers = new(StringComparer.Ordinal);

    public bool IsAvailable(string model) => _providers.ContainsKey(ModelProviders.ProviderOf(model));

    public bool Has(string provider) => _providers.ContainsKey(provider);

    public bool IsEmpty => _providers.Count == 0;

    public IModelProvider For(string model) =>
        _providers.TryGetValue(ModelProviders.ProviderOf(model), out var provider)
            ? provider
            : throw new InvalidOperationException($"No API key configured for {ModelProviders.ProviderOf(model)} (model {model}).");

    public void Set(IModelProvider provider)
    {
        if (_providers.Remove(provider.Name, out var previous))
        {
            previous.Dispose();
        }

        _providers[provider.Name] = provider;
    }

    public void Remove(string provider)
    {
        if (_providers.Remove(provider, out var previous))
        {
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }
}
