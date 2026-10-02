using System.Net;
using System.Net.Http.Headers;

namespace ShunchakiAi.Api;

/// <summary>
/// Thin, AOT-friendly client for the Anthropic Messages API built on <see cref="HttpClient"/>.
/// Retries transient failures (429, 5xx, 529, network errors) with exponential backoff.
/// </summary>
public sealed class AnthropicClient : IDisposable
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const int MaxRetries = 3;

    private readonly HttpClient _http;

    public AnthropicClient(Uri baseUrl, string apiKey, bool serverFallback)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUrl,
            // Long agentic turns at high effort can legitimately take several minutes.
            Timeout = TimeSpan.FromMinutes(10),
        };

        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        if (serverFallback)
        {
            _http.DefaultRequestHeaders.Add("anthropic-beta", FallbackBeta);
        }

        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("shunchaki-ai", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ModelResponse> CreateMessageAsync(MessageRequest request, CancellationToken cancellationToken)
    {
        var payload = request.ToUtf8Json();

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = new ByteArrayContent(payload),
            };
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await BackoffAsync(attempt, retryAfter: null, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout elapsed (not a user cancellation).
                if (attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, retryAfter: null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new TimeoutException("The API request timed out.", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return ModelResponse.Parse(body);
                }

                if (IsRetryable(response.StatusCode) && attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, response.Headers.RetryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw AnthropicApiException.FromResponse((int)response.StatusCode, body);
            }
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict
        || (int)status >= 500;

    private static Task BackoffAsync(int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

        delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60));
        return Task.Delay(delay, cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}
