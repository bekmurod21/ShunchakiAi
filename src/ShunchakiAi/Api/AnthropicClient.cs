namespace ShunchakiAi.Api;

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>) over <see cref="HttpClient"/>.</summary>
public sealed class AnthropicClient : IModelProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly HttpClient _http;

    public AnthropicClient(Uri baseUrl, string apiKey)
    {
        _http = HttpSender.CreateClient(baseUrl);
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public string Name => ModelProviders.Anthropic;

    public async Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken)
    {
        var body = await HttpSender.PostJsonAsync(
            _http,
            "v1/messages",
            request.ToAnthropicJson(),
            message =>
            {
                if (request.ServerFallback)
                {
                    message.Headers.Add("anthropic-beta", FallbackBeta);
                }
            },
            ModelApiException.FromAnthropic,
            maxRetries,
            cancellationToken).ConfigureAwait(false);

        return ModelResponse.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}
