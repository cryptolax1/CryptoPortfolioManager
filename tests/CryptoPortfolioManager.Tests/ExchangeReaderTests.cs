using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Infrastructure.Exchanges;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoPortfolioManager.Tests;

/// <summary>Answers every request with a canned response and records what was sent, so the clients can be tested offline.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

public class ExchangeReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static (BitvavoExchangeReader Reader, StubHttpHandler Handler) Bitvavo(Func<HttpRequestMessage, HttpResponseMessage> respond, bool withKeys = true)
    {
        var config = withKeys
            ? Config(("Exchange:Bitvavo:ApiKey", "my-key"), ("Exchange:Bitvavo:ApiSecret", "my-secret"))
            : Config();
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitvavo.com/") };
        return (new BitvavoExchangeReader(http, config, NullLogger<BitvavoExchangeReader>.Instance), handler);
    }

    [Fact]
    public async Task Bitvavo_ReadsAvailablePlusMoneyInOpenOrders_AndSignsTheRequest()
    {
        const string body = """
            [
              { "symbol": "BTC", "available": "1.5", "inOrder": "0.25" },
              { "symbol": "CRO", "available": "100", "inOrder": "0" },
              { "symbol": "EMPTY", "available": "0", "inOrder": "0" }
            ]
            """;
        var (reader, handler) = Bitvavo(_ => StubHttpHandler.Json(body));

        var holdings = await reader.GetBalancesAsync(Ct);

        Assert.Equal(2, holdings.Count); // the zero balance is not reported
        Assert.Equal(1.75m, holdings.Single(h => h.Symbol == "BTC").Quantity);
        Assert.Equal(100m, holdings.Single(h => h.Symbol == "CRO").Quantity);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.bitvavo.com/v2/balance", request.RequestUri!.ToString());
        Assert.Equal("my-key", request.Headers.GetValues("Bitvavo-Access-Key").Single());

        // The documented signature: hex HMAC-SHA256 of "GET /v2/balance" with the secret.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("my-secret"), System.Text.Encoding.UTF8.GetBytes("GET /v2/balance")));
        Assert.Equal(expected, request.Headers.GetValues("Bitvavo-Access-Signature").Single());
    }

    [Fact]
    public async Task Bitvavo_WithoutCredentials_FailsWithInstructions()
    {
        var (reader, handler) = Bitvavo(_ => StubHttpHandler.Json("[]"), withKeys: false);

        Assert.False(reader.IsConfigured);
        var ex = await Assert.ThrowsAsync<ExchangeBalanceException>(() => reader.GetBalancesAsync(Ct));
        Assert.Contains("Exchange:Bitvavo:ApiKey", ex.Message);
        Assert.Empty(handler.Requests); // nothing is sent without a key
    }

    [Fact]
    public async Task Bitvavo_RejectedKey_IsReportedAsSuch()
    {
        var (reader, _) = Bitvavo(_ => StubHttpHandler.Json("{\"errorCode\":\"authentication-error\"}", 401));

        var ex = await Assert.ThrowsAsync<ExchangeBalanceException>(() => reader.GetBalancesAsync(Ct));
        Assert.Contains("401", ex.Message);
    }

    private static (CryptoComExchangeReader Reader, StubHttpHandler Handler) CryptoCom(Func<HttpRequestMessage, HttpResponseMessage> respond, bool withKeys = true)
    {
        var config = withKeys
            ? Config(("Exchange:Crypto.com:ApiKey", "key"), ("Exchange:Crypto.com:ApiSecret", "secret"))
            : Config();
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.crypto.com/") };
        return (new CryptoComExchangeReader(http, config, NullLogger<CryptoComExchangeReader>.Instance), handler);
    }

    [Fact]
    public async Task CryptoCom_SumsAccountsPerCurrency_AndStripsTheLendPrefix()
    {
        const string body = """
            {
              "code": 0,
              "result": {
                "data": [
                  { "currency": "CRO", "balance": "50" },
                  { "currency": "CRO", "balance": "25.5" },
                  { "currency": "LDBTC", "balance": "0.1" },
                  { "currency": "ETH", "balance": 0 }
                ]
              }
            }
            """;
        var (reader, handler) = CryptoCom(_ => StubHttpHandler.Json(body));

        var holdings = await reader.GetBalancesAsync(Ct);

        Assert.Equal(2, holdings.Count);
        Assert.Equal(75.5m, holdings.Single(h => h.Symbol == "CRO").Quantity);
        Assert.Equal(0.1m, holdings.Single(h => h.Symbol == "BTC").Quantity); // "LDBTC" is lent-out BTC

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.crypto.com/v1/crypto/accounts", request.RequestUri!.ToString());
        Assert.Equal("key", request.Headers.GetValues("api_key").Single());

        // The signed text is requestId + ts + method + path + host; its HMAC must match the "sig" header.
        var requestId = request.Headers.GetValues("id").Single();
        var timestamp = request.Headers.GetValues("ts").Single();
        var signed = CryptoComExchangeReader.SignedText(requestId, timestamp, "GET", "/v1/crypto/accounts", "api.crypto.com");
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("secret"), System.Text.Encoding.UTF8.GetBytes(signed)));
        Assert.Equal(expected, request.Headers.GetValues("sig").Single());
    }

    [Fact]
    public async Task CryptoCom_UnexpectedShape_IsReported()
    {
        var (reader, _) = CryptoCom(_ => StubHttpHandler.Json("{\"code\":0}"));

        var ex = await Assert.ThrowsAsync<ExchangeBalanceException>(() => reader.GetBalancesAsync(Ct));
        Assert.Contains("unexpected response", ex.Message);
    }

    [Fact]
    public void Catalogue_MatchesUserTypedSources_ToleratesPunctuation()
    {
        Assert.Equal(ExchangeCatalog.Bitvavo, ExchangeCatalog.Match("bitvavo"));
        Assert.Equal(ExchangeCatalog.CryptoCom, ExchangeCatalog.Match("crypto com"));
        Assert.Equal(ExchangeCatalog.CryptoCom, ExchangeCatalog.Match("Crypto.com"));
        Assert.Null(ExchangeCatalog.Match("Binance"));
        Assert.Null(ExchangeCatalog.Match(""));
    }
}
