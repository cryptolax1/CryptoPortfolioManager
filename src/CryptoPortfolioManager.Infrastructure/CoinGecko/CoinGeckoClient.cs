using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.CoinGecko;

/// <summary>
/// CoinGecko Demo API client. Uses only documented endpoints:
/// <c>GET simple/price</c> and <c>GET coins/{id}/market_chart/range</c>.
/// </summary>
public sealed partial class CoinGeckoClient(
    HttpClient http,
    IOptions<CoinGeckoOptions> options,
    CoinGeckoRequestThrottle throttle,
    ILogger<CoinGeckoClient> logger) : IPriceProvider
{
    public const string Name = "CoinGecko";
    public const string ApiKeyHeader = "x-cg-demo-api-key";

    private const int PriceDecimals = 18;

    public string SourceName => Name;

    public async Task<IReadOnlyDictionary<string, PriceQuote>> GetCurrentUsdPricesAsync(
        IReadOnlyCollection<string> providerIds, CancellationToken ct = default)
    {
        var ids = providerIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return result;

        var url = $"simple/price?ids={string.Join(',', ids.Select(Uri.EscapeDataString))}" +
                  "&vs_currencies=usd&include_last_updated_at=true&precision=full";

        using var doc = await GetJsonAsync(url, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new PriceProviderException(PriceProviderErrorKind.BadResponse, "Unexpected response format from CoinGecko (simple/price).");

        var entries = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            // Unknown ids and coins without a USD price are simply absent or null.
            if (!entries.TryGetValue(id, out var entry) || entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("usd", out var usd) || !TryReadPrice(usd, out var price)) continue;

            DateTime? updated = null;
            if (entry.TryGetProperty("last_updated_at", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var seconds))
                updated = FromUnix(seconds * 1000);

            result[id] = new PriceQuote(price, updated);
        }

        return result;
    }

    public async Task<IReadOnlyList<PricePoint>> GetHistoricalUsdPricesAsync(
        string providerId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (toUtc <= fromUtc) return [];

        var from = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var to = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        // interval=daily: one point per day (00:00 UTC), regardless of range length.
        var url = $"coins/{Uri.EscapeDataString(providerId.Trim())}/market_chart/range" +
                  $"?vs_currency=usd&from={from.ToString(CultureInfo.InvariantCulture)}&to={to.ToString(CultureInfo.InvariantCulture)}" +
                  "&interval=daily&precision=full";

        using var doc = await GetJsonAsync(url, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array)
            throw new PriceProviderException(PriceProviderErrorKind.BadResponse, "Unexpected response format from CoinGecko (market_chart/range).");

        var points = new Dictionary<DateTime, PricePoint>();
        var skipped = 0;
        foreach (var item in prices.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2 ||
                item[0].ValueKind != JsonValueKind.Number || !item[0].TryGetDouble(out var ms) ||
                FromUnix((long)ms) is not { } timestamp ||
                !TryReadPrice(item[1], out var price))
            {
                skipped++;
                continue;
            }

            points[timestamp] = new PricePoint(timestamp, price);
        }

        if (skipped > 0)
            logger.LogWarning("CoinGecko history for {CoinId}: {Skipped} points without a valid price were ignored", providerId, skipped);

        return points.Values.OrderBy(p => p.TimestampUtc).ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            throw new PriceProviderException(PriceProviderErrorKind.NotConfigured,
                "CoinGecko API key is not configured. Set it with: dotnet user-secrets set \"CoinGecko:ApiKey\" \"<key>\" (see README).");

        for (var attempt = 0; ; attempt++)
        {
            var canRetry = attempt < opt.MaxRetries;
            await throttle.WaitAsync(opt.MinRequestInterval, ct);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Add(ApiKeyHeader, opt.ApiKey);
                request.Headers.Accept.ParseAdd("application/json");
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                if (canRetry) { await DelayAsync(Backoff(opt, attempt), ct); continue; }
                throw new PriceProviderException(PriceProviderErrorKind.Network, $"Network error: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient reports its own timeout as a cancellation.
                if (canRetry) { await DelayAsync(Backoff(opt, attempt), ct); continue; }
                throw new PriceProviderException(PriceProviderErrorKind.Timeout, "CoinGecko request timed out.", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return await ParseAsync(response, ct);

                var status = (int)response.StatusCode;
                var snippet = await ReadSnippetAsync(response, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = RetryAfter(response);
                    var delay = retryAfter ?? Backoff(opt, attempt);
                    if (canRetry && delay <= opt.MaxRetryDelay)
                    {
                        logger.LogWarning("CoinGecko rate limit hit (429); waiting {Delay} before retry", delay);
                        await DelayAsync(delay, ct);
                        continue;
                    }

                    throw new PriceProviderException(PriceProviderErrorKind.RateLimited,
                        $"CoinGecko rate limit exceeded (HTTP 429){(retryAfter is { } r ? $", retry after {r.TotalSeconds:0}s" : "")}. Try again later.",
                        retryAfter: retryAfter);
                }

                if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    if (canRetry) { await DelayAsync(Backoff(opt, attempt), ct); continue; }
                    throw new PriceProviderException(PriceProviderErrorKind.ServerError, $"CoinGecko server error (HTTP {status}). {snippet}".Trim());
                }

                throw ClientError(response.StatusCode, status, snippet);
            }
        }
    }

    private static PriceProviderException ClientError(HttpStatusCode code, int status, string snippet)
    {
        var detail = snippet.Length > 0 ? $" {snippet}" : "";

        // Error codes 10005 (plan restricted) and 10012 (time range not allowed) are documented by CoinGecko.
        if (snippet.Contains("10005") || snippet.Contains("10012"))
            return new(PriceProviderErrorKind.PlanRestricted, $"Request not allowed on this CoinGecko plan (HTTP {status}).{detail}");

        return code switch
        {
            HttpStatusCode.Unauthorized => new(PriceProviderErrorKind.Unauthorized, $"CoinGecko rejected the API key (HTTP 401). Check CoinGecko:ApiKey.{detail}"),
            HttpStatusCode.Forbidden => new(PriceProviderErrorKind.PlanRestricted, $"CoinGecko denied access (HTTP 403).{detail}"),
            HttpStatusCode.NotFound => new(PriceProviderErrorKind.NotFound, $"CoinGecko does not know this coin id (HTTP 404).{detail}"),
            _ => new(PriceProviderErrorKind.BadRequest, $"CoinGecko rejected the request (HTTP {status}).{detail}")
        };
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new PriceProviderException(PriceProviderErrorKind.BadResponse, "CoinGecko returned an unreadable response.", ex);
        }
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            body = Whitespace().Replace(body, " ").Trim();
            return body.Length <= 200 ? body : body[..200] + "…";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        return null;
    }

    private static TimeSpan Backoff(CoinGeckoOptions opt, int attempt)
    {
        var delay = TimeSpan.FromTicks(opt.RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 10)));
        return delay > opt.MaxRetryDelay ? opt.MaxRetryDelay : delay;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, ct) : Task.CompletedTask;

    private static bool TryReadPrice(JsonElement element, out decimal price)
    {
        price = 0m;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value)) return false;

        price = Math.Round(value, PriceDecimals);
        return price > 0m;
    }

    private static DateTime? FromUnix(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
