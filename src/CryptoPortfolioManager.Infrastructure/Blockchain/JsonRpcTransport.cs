using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// POSTs a JSON request to a node and returns the JSON body. Retries 429, 5xx, time-outs and network errors with
/// exponential backoff; every other failure is final and reported as a <see cref="WalletBalanceException"/>.
/// </summary>
internal sealed class JsonRpcTransport(HttpClient http, BlockchainRpcOptions options)
{
    /// <param name="rateLimitMessage">
    /// Inspects a body that arrived with HTTP 200 and returns a message when the node reports a failure that is worth
    /// retrying (rate limiting). Return null otherwise; the caller interprets the body.
    /// </param>
    public async Task<JsonObject> PostAsync(Uri endpoint, JsonObject request, Func<JsonObject, string?> rateLimitMessage, CancellationToken ct)
    {
        var body = await SendAsync(() => http.PostAsJsonAsync(endpoint, request, ct),
            b => b is JsonObject o ? rateLimitMessage(o) : null, notFoundIsEmpty: false, ct);
        return body as JsonObject ?? throw new WalletBalanceException("The node returned an unexpected response.");
    }

    /// <summary>POSTs and returns the JSON body as is (a REST API may answer with an array).</summary>
    public async Task<JsonNode> PostForNodeAsync(Uri endpoint, JsonObject request, CancellationToken ct) =>
        await SendAsync(() => http.PostAsJsonAsync(endpoint, request, ct), _ => null, notFoundIsEmpty: false, ct)
        ?? throw new WalletBalanceException("The node returned an empty response.");

    /// <summary>GETs a REST resource. Returns null for HTTP 404 when <paramref name="notFoundIsEmpty"/> is set (an unknown account is a valid, empty answer).</summary>
    public Task<JsonNode?> GetAsync(Uri endpoint, bool notFoundIsEmpty, CancellationToken ct) =>
        SendAsync(() => http.GetAsync(endpoint, ct), _ => null, notFoundIsEmpty, ct);

    private async Task<JsonNode?> SendAsync(Func<Task<HttpResponseMessage>> send, Func<JsonNode, string?> rateLimitMessage, bool notFoundIsEmpty, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            string failure;
            try
            {
                using var response = await send();

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct)
                        ?? throw new WalletBalanceException("The node returned an empty response.");

                    var limited = rateLimitMessage(body);
                    if (limited is null) return body;

                    failure = limited;
                }
                else if (notFoundIsEmpty && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                else if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                         or >= HttpStatusCode.InternalServerError)
                {
                    failure = $"The node answered HTTP {(int)response.StatusCode}.";
                }
                else
                {
                    throw new WalletBalanceException($"The node answered HTTP {(int)response.StatusCode}.");
                }
            }
            catch (HttpRequestException ex)
            {
                failure = $"Network error: {ex.Message}";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = "The request timed out.";
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                throw new WalletBalanceException("The node returned an unexpected response.", ex);
            }

            if (attempt >= options.MaxRetries)
                throw new WalletBalanceException(failure);

            await Task.Delay(options.RetryBaseDelay * Math.Pow(2, attempt), ct);
        }
    }

    public static Uri Endpoint(BlockchainRpcOptions options, Core.Entities.Chain chain) =>
        options.RpcUrls.TryGetValue(chain.ToString(), out var url) && Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            ? endpoint
            : throw new WalletBalanceException($"No node is configured for {chain}.");

    /// <summary>Appends a path to a configured base URL, keeping any path the base already has.</summary>
    public static Uri Combine(Uri baseUri, string path) => new($"{baseUri.AbsoluteUri.TrimEnd('/')}/{path}");

    public static JsonObject Rpc(string method, params object[] parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = method,
        ["params"] = new JsonArray(parameters.Select(p => p is JsonNode n ? n : JsonValue.Create(p)).ToArray())
    };

    public static bool LooksRateLimited(string? message) =>
        message is not null && (message.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("too many", StringComparison.OrdinalIgnoreCase));
}
