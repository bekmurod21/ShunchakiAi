using System.Net.Http.Headers;

namespace ShunchakiAi.Api;

/// <summary>
/// Anthropic Messages API (<c>POST /v1/messages</c>) over <see cref="HttpClient"/>.
/// Accepts either an API key (<c>sk-ant-api…</c>, sent as <c>x-api-key</c>) or an OAuth access
/// token (<c>sk-ant-oat…</c>, e.g. from <c>ant auth print-credentials --access-token</c>), which is
/// sent as a bearer token together with the OAuth beta header.
/// </summary>
public sealed class AnthropicClient : IModelProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly bool _isOAuthToken;

    public AnthropicClient(Uri baseUrl, string credential)
    {
        _http = HttpSender.CreateClient(baseUrl);
        _isOAuthToken = IsOAuthToken(credential);
        if (_isOAuthToken)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        else
        {
            _http.DefaultRequestHeaders.Add("x-api-key", credential);
        }

        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public string Name => ModelProviders.Anthropic;

    public static bool IsOAuthToken(string credential) => credential.StartsWith("sk-ant-oat", StringComparison.Ordinal);

    public async Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken)
    {
        var betas = new List<string>(2);
        if (request.ServerFallback)
        {
            betas.Add(FallbackBeta);
        }

        var body = await HttpSender.PostJsonAsync(
            _http,
            "v1/messages",
            request.ToAnthropicJson(),
            message => AddBetas(message, betas),
            ModelApiException.FromAnthropic,
            maxRetries,
            cancellationToken).ConfigureAwait(false);

        return ModelResponse.Parse(body);
    }

    public Task<KeyCheck> CheckKeyAsync(CancellationToken cancellationToken) =>
        HttpSender.CheckAsync(_http, "v1/models?limit=1", message => AddBetas(message, []), cancellationToken);

    private void AddBetas(HttpRequestMessage message, List<string> betas)
    {
        if (_isOAuthToken)
        {
            betas = [.. betas, OAuthBeta];
        }

        if (betas.Count > 0)
        {
            message.Headers.Add("anthropic-beta", string.Join(',', betas));
        }
    }

    public void Dispose() => _http.Dispose();
}
