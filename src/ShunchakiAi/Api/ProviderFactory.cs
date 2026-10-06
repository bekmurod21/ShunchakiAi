using ShunchakiAi.Configuration;

namespace ShunchakiAi.Api;

/// <summary>Creates the provider client for a stored credential.</summary>
public static class ProviderFactory
{
    public static IModelProvider Create(string provider, string secret, AgentOptions options) => provider switch
    {
        ModelProviders.Gemini => new GeminiClient(options.GeminiBaseUrl, secret),
        _ => new AnthropicClient(options.AnthropicBaseUrl, secret),
    };
}
