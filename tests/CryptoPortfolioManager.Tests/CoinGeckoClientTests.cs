using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Infrastructure.CoinGecko;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Tests;

public class CoinGeckoClientTests
{
    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var respond = responses[Math.Min(_next++, responses.Length - 1)];
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (CoinGeckoClient Client, StubHandler Handler) Create(
        StubHandler handler, string? apiKey = "test-key", int maxRetries = 2)
    {
        var options = Options.Create(new CoinGeckoOptions
        {
            ApiKey = apiKey,
            MaxRetries = maxRetries,
            RetryBaseDelay = TimeSpan.Zero,
            MinRequestInterval = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.FromSeconds(60)
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/api/v3/") };
        return (new CoinGeckoClient(http, options, new CoinGeckoRequestThrottle(), NullLogger<CoinGeckoClient>.Instance), handler);
    }

    [Fact]
    public async Task CurrentPrices_AreRequestedInOneCall_WithApiKeyHeader_AndParsed()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("""
            {"bitcoin":{"usd":81832.71348330718,"last_updated_at":1791989309},"ethereum":{"usd":2500.5}}
            """)));

        var prices = await client.GetCurrentUsdPricesAsync(["bitcoin", "ethereum", "bitcoin"]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-key", request.Headers.GetValues(CoinGeckoClient.ApiKeyHeader).Single());
        Assert.DoesNotContain("test-key", request.RequestUri!.ToString());
        Assert.Contains("ids=bitcoin,ethereum", request.RequestUri.Query);
        Assert.Contains("vs_currencies=usd", request.RequestUri.Query);
        Assert.Equal(81832.71348330718m, prices["bitcoin"].PriceUsd);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791989309).UtcDateTime, prices["bitcoin"].TimestampUtc);
        Assert.Null(prices["ethereum"].TimestampUtc);
    }

    [Fact]
    public async Task CurrentPrices_MissingNullZeroAndNegative_AreOmitted()
    {
        var (client, _) = Create(new StubHandler(_ => Json("""
            {"good":{"usd":1.5},"zero":{"usd":0},"negative":{"usd":-3},"nullprice":{"usd":null},"nousd":{"eur":2},"text":{"usd":"abc"}}
            """)));

        var prices = await client.GetCurrentUsdPricesAsync(["good", "zero", "negative", "nullprice", "nousd", "text", "unknown"]);

        Assert.Equal(["good"], prices.Keys);
    }

    [Fact]
    public async Task CurrentPrices_VerySmallPrice_KeepsPrecision()
    {
        var (client, _) = Create(new StubHandler(_ => Json("""{"shib":{"usd":0.00000879123456}}""")));

        var prices = await client.GetCurrentUsdPricesAsync(["shib"]);

        Assert.Equal(0.00000879123456m, prices["shib"].PriceUsd);
    }

    [Fact]
    public async Task CurrentPrices_WithoutIds_MakesNoRequest()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("{}")));

        Assert.Empty(await client.GetCurrentUsdPricesAsync([" ", ""]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task History_ParsesDailyPoints_SkipsInvalid_AndSortsAscending()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("""
            {"prices":[[1783468800000,63335.3],[1783382400000,64001.2],[1783555200000,null],[1783641600000,0],[1783728000000,-1],["x",5],[1783814400000,63791.35]]}
            """)));

        var from = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        var points = await client.GetHistoricalUsdPricesAsync("bitcoin", from, from.AddDays(10));

        Assert.Equal(3, points.Count);
        Assert.True(points[0].TimestampUtc < points[1].TimestampUtc);
        Assert.All(points, p => Assert.True(p.PriceUsd > 0m));

        var uri = handler.Requests.Single().RequestUri!;
        Assert.Contains("/coins/bitcoin/market_chart/range", uri.AbsolutePath);
        Assert.Contains($"from={new DateTimeOffset(from).ToUnixTimeSeconds()}", uri.Query);
        Assert.Contains("interval=daily", uri.Query);
    }

    [Fact]
    public async Task History_WithoutPricesProperty_IsBadResponse()
    {
        var (client, _) = Create(new StubHandler(_ => Json("""{"unexpected":true}""")));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() =>
            client.GetHistoricalUsdPricesAsync("bitcoin", DateTime.UtcNow.AddDays(-5), DateTime.UtcNow));

        Assert.Equal(PriceProviderErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task MalformedJson_IsBadResponse()
    {
        var (client, _) = Create(new StubHandler(_ => Json("<html>oops")));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task MissingApiKey_FailsBeforeAnyRequest()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("{}")), apiKey: "  ");

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.NotConfigured, ex.Kind);
        Assert.True(ex.StopsRun);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimit_IsRetried_HonoringRetryAfter_ThenSucceeds()
    {
        var (client, handler) = Create(new StubHandler(
            _ =>
            {
                var response = Json("""{"status":{"error_code":429}}""", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            },
            _ => Json("""{"bitcoin":{"usd":1}}""")));

        var prices = await client.GetCurrentUsdPricesAsync(["bitcoin"]);

        Assert.Equal(1m, prices["bitcoin"].PriceUsd);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimit_AfterRetriesExhausted_ThrowsRateLimited()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("{}", HttpStatusCode.TooManyRequests)), maxRetries: 2);

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.RateLimited, ex.Kind);
        Assert.True(ex.StopsRun);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimit_WithVeryLongRetryAfter_FailsImmediately()
    {
        var (client, handler) = Create(new StubHandler(_ =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        }));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(TimeSpan.FromMinutes(10), ex.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerError_IsRetried_ThenReported()
    {
        var (client, handler) = Create(new StubHandler(_ => Json("down", HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.ServerError, ex.Kind);
        Assert.False(ex.StopsRun);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PriceProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, PriceProviderErrorKind.PlanRestricted)]
    [InlineData(HttpStatusCode.NotFound, PriceProviderErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, PriceProviderErrorKind.BadRequest)]
    public async Task ClientErrors_AreMappedAndNotRetried(HttpStatusCode status, PriceProviderErrorKind expected)
    {
        var (client, handler) = Create(new StubHandler(_ => Json("{}", status)));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(expected, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TimeRangeError_10012_IsPlanRestricted()
    {
        var (client, _) = Create(new StubHandler(_ => Json("""{"status":{"error_code":10012}}""", HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() =>
            client.GetHistoricalUsdPricesAsync("bitcoin", DateTime.UtcNow.AddDays(-900), DateTime.UtcNow));

        Assert.Equal(PriceProviderErrorKind.PlanRestricted, ex.Kind);
    }

    [Fact]
    public async Task Timeout_IsRetried_ThenReportedAsTimeout()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; throw new TaskCanceledException("timeout"); });
        var (client, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.Timeout, ex.Kind);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NetworkError_IsReported()
    {
        var (client, _) = Create(new StubHandler(_ => throw new HttpRequestException("no route to host")), maxRetries: 0);

        var ex = await Assert.ThrowsAsync<PriceProviderException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"]));

        Assert.Equal(PriceProviderErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task UserCancellation_IsNotTreatedAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        var (client, _) = Create(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCurrentUsdPricesAsync(["bitcoin"], cts.Token));
    }

    [Fact]
    public async Task Requests_AreSpacedByTheThrottle()
    {
        var options = Options.Create(new CoinGeckoOptions { ApiKey = "k", MinRequestInterval = TimeSpan.FromMilliseconds(200) });
        var http = new HttpClient(new StubHandler(_ => Json("{}"))) { BaseAddress = new Uri("https://api.example.test/api/v3/") };
        var client = new CoinGeckoClient(http, options, new CoinGeckoRequestThrottle(), NullLogger<CoinGeckoClient>.Instance);

        var started = DateTime.UtcNow;
        await client.GetCurrentUsdPricesAsync(["a"]);
        await client.GetCurrentUsdPricesAsync(["b"]);
        await client.GetCurrentUsdPricesAsync(["c"]);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(380));
    }
}
