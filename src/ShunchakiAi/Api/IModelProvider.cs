namespace ShunchakiAi.Api;

/// <summary>
/// One LLM backend. Every provider takes the same <see cref="MessageRequest"/> (history in the
/// canonical Messages-API format) and returns a <see cref="ModelResponse"/> in that same format,
/// so the conversation never depends on which provider produced a turn.
/// </summary>
public interface IModelProvider : IDisposable
{
    string Name { get; }

    Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken);
}

public static class ModelProviders
{
    public const string Anthropic = "Anthropic";
    public const string Gemini = "Gemini";

    public static string ProviderOf(string model) =>
        model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? Gemini : Anthropic;
}

/// <summary>Routes each model id to the provider that serves it.</summary>
public sealed class ProviderRegistry(IEnumerable<IModelProvider> providers) : IDisposable
{
    private readonly Dictionary<string, IModelProvider> _providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public bool IsAvailable(string model) => _providers.ContainsKey(ModelProviders.ProviderOf(model));

    public IModelProvider For(string model) =>
        _providers.TryGetValue(ModelProviders.ProviderOf(model), out var provider)
            ? provider
            : throw new InvalidOperationException($"No API key configured for {ModelProviders.ProviderOf(model)} (model {model}).");

    public void Dispose()
    {
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }
    }
}
