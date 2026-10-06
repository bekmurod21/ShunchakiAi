using System.Net;
using System.Net.Http.Headers;

namespace ShunchakiAi.Api;

/// <summary>
/// POSTs a JSON payload with retries for transient failures (429, 408, 409, 5xx, network
/// errors, timeouts), honouring <c>retry-after</c>. Shared by every provider client.
/// </summary>
internal static class HttpSender
{
    public static HttpClient CreateClient(Uri baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUrl,
            // Long agentic turns at high effort can legitimately take several minutes.
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("shunchaki", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static async Task<string> PostJsonAsync(
        HttpClient http,
        string path,
        byte[] payload,
        Action<HttpRequestMessage>? configure,
        Func<int, string, TimeSpan?, ModelApiException> errorFactory,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            configure?.Invoke(request);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await BackoffAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout elapsed (not a user cancellation).
                if (attempt < maxRetries)
                {
                    await BackoffAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new TimeoutException("The API request timed out.", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return body;
                }

                var retryAfter = RetryDelay(response.Headers.RetryAfter);
                if (IsRetryable(response.StatusCode) && attempt < maxRetries)
                {
                    await BackoffAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw errorFactory((int)response.StatusCode, body, retryAfter);
            }
        }
    }

    /// <summary>
    /// Single GET used to verify credentials. Maps the outcome to a <see cref="KeyCheck"/>
    /// without retries: 2xx is valid, 400/401/403 mean the key was rejected.
    /// </summary>
    public static async Task<KeyCheck> CheckAsync(HttpClient http, string path, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            configure?.Invoke(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new KeyCheck(KeyCheckResult.Valid, "key accepted"),
                400 or 401 or 403 => new KeyCheck(KeyCheckResult.Invalid, $"key rejected (HTTP {status})"),
                _ => new KeyCheck(KeyCheckResult.Unknown, $"could not verify the key (HTTP {status})"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KeyCheck(KeyCheckResult.Unknown, "could not verify the key (timed out)");
        }
        catch (HttpRequestException ex)
        {
            return new KeyCheck(KeyCheckResult.Unknown, $"could not verify the key ({ex.Message})");
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict
        || (int)status >= 500;

    private static TimeSpan? RetryDelay(RetryConditionHeaderValue? retryAfter) =>
        retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);

    private static Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60));
        return Task.Delay(delay, cancellationToken);
    }
}
