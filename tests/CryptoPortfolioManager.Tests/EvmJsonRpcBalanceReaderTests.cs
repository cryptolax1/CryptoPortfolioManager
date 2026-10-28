using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Infrastructure.Blockchain;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Tests;

public class EvmJsonRpcBalanceReaderTests
{
    private const string Wallet = "0x59711D16ff7F32EAe184BD1B892C7e6d6C058bFc";
    private const string Usdc = "0xc21223249CA28397B4B6541dfFaEcC539BfF0c59";

    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<(Uri Uri, JsonNode Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!));
            return responses[Math.Min(_next++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Rpc(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Result(string hex) => Rpc($$"""{"jsonrpc":"2.0","id":1,"result":"{{hex}}"}""");

    private static (EvmJsonRpcBalanceReader Reader, StubHandler Handler) Create(StubHandler handler, int maxRetries = 2) =>
        (new EvmJsonRpcBalanceReader(new HttpClient(handler), Options.Create(new BlockchainRpcOptions
        {
            MaxRetries = maxRetries, RetryBaseDelay = TimeSpan.Zero
        })), handler);

    private static string Hex(BigInteger value) => "0x" + value.ToString("x").TrimStart('0').PadLeft(1, '0');

    [Fact]
    public async Task NativeBalance_UsesEthGetBalance_AndAppliesDecimals()
    {
        var (reader, handler) = Create(new StubHandler(_ => Result(Hex(BigInteger.Parse("1500000000000000000")))));

        var balance = await reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken);

        Assert.Equal(1.5m, balance);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://evm.cronos.org/", request.Uri.ToString());
        Assert.Equal("eth_getBalance", request.Body["method"]!.GetValue<string>());
        Assert.Equal(Wallet, request.Body["params"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task TokenBalance_UsesBalanceOfCall_WithPaddedAddress_AndTokenDecimals()
    {
        var (reader, handler) = Create(new StubHandler(_ => Result(Hex(12_345_678))));

        var balance = await reader.GetBalanceAsync(Chain.Cronos, Wallet, Usdc, 6, TestContext.Current.CancellationToken);

        Assert.Equal(12.345678m, balance);
        var call = handler.Requests.Single().Body;
        Assert.Equal("eth_call", call["method"]!.GetValue<string>());
        Assert.Equal(Usdc, call["params"]![0]!["to"]!.GetValue<string>());
        Assert.Equal("0x70a08231" + new string('0', 24) + Wallet[2..].ToLowerInvariant(),
            call["params"]![0]!["data"]!.GetValue<string>());
    }

        [Fact]
    public async Task BnbSmartChain_UsesItsOwnEndpoint()
    {
        var (reader, handler) = Create(new StubHandler(_ => Result("0x0")));

        var balance = await reader.GetBalanceAsync(Chain.BnbSmartChain, Wallet, null, 18, TestContext.Current.CancellationToken);

        Assert.Equal(0m, balance);
        Assert.Equal("bsc-dataseed.binance.org", handler.Requests.Single().Uri.Host);
    }

        [Fact]
    public async Task Flare_ReadsNativeFlrAndErc20OverItsOwnEndpoint()
    {
        var (reader, handler) = Create(new StubHandler(
            _ => Result(Hex(BigInteger.Parse("2500000000000000000"))), // 2.5 FLR (18 decimals)
            _ => Result(Hex(12_345_678))));                            // 12.345678 of a 6-decimal token

        Assert.True(reader.Supports(Chain.Flare));

        var native = await reader.GetBalanceAsync(Chain.Flare, Wallet, null, 18, TestContext.Current.CancellationToken);
        Assert.Equal(2.5m, native);
        Assert.Equal("https://flare-api.flare.network/ext/C/rpc", handler.Requests[0].Uri.ToString());
        Assert.Equal("eth_getBalance", handler.Requests[0].Body["method"]!.GetValue<string>());

        var token = await reader.GetBalanceAsync(Chain.Flare, Wallet, Usdc, 6, TestContext.Current.CancellationToken);
        Assert.Equal(12.345678m, token);
        Assert.Equal("eth_call", handler.Requests[1].Body["method"]!.GetValue<string>());
        Assert.Equal(Usdc, handler.Requests[1].Body["params"]![0]!["to"]!.GetValue<string>());
    }

    [Fact]
    public async Task Balance_KeepsFullPrecision_OfRealisticWeiAmount()
    {
        // 0x21647ca9aa7fcfbb1b3 is a real balance read from the Cronos node.
        var (reader, _) = Create(new StubHandler(_ => Result("0x21647ca9aa7fcfbb1b3")));

        var balance = await reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken);

        Assert.Equal(9855.734452529183568307m, balance);
    }

    [Theory]
    [InlineData("1", 0, "1")]
    [InlineData("1", 18, "0.000000000000000001")]
    [InlineData("123456789012345678901234567890", 18, "123456789012.34567890123456789")]
    [InlineData("5", 30, "0")]
    public void ToTokens_ConvertsWithoutFloatingPoint(string raw, int decimals, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            EvmJsonRpcBalanceReader.ToTokens(BigInteger.Parse(raw), decimals));
    }

    [Fact]
    public async Task NonContractAddress_IsAnError_NotAZeroBalance()
    {
        var (reader, _) = Create(new StubHandler(_ => Result("0x")));

        var ex = await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Cronos, Wallet, Usdc, 6, TestContext.Current.CancellationToken));

        Assert.Contains("not a contract", ex.Message);
    }

    [Fact]
    public async Task RateLimit_IsRetried_ThenSucceeds()
    {
        var (reader, handler) = Create(new StubHandler(
            _ => Rpc("", HttpStatusCode.TooManyRequests),
            _ => Rpc("", HttpStatusCode.BadGateway),
            _ => Result("0xde0b6b3a7640000")));

        var balance = await reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken);

        Assert.Equal(1m, balance);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PersistentServerError_FailsAfterRetries()
    {
        var (reader, handler) = Create(new StubHandler(_ => Rpc("", HttpStatusCode.InternalServerError)), maxRetries: 1);

        var ex = await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken));

        Assert.Contains("500", ex.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientError_IsNotRetried()
    {
        var (reader, handler) = Create(new StubHandler(_ => Rpc("", HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task JsonRpcError_BecomesException_WithNodeMessage()
    {
        var (reader, handler) = Create(new StubHandler(_ => Rpc("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"invalid argument 0"}}""")));

        var ex = await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken));

        Assert.Contains("invalid argument 0", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":"zzz"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":null}""")]
    public async Task InvalidResponses_BecomeWalletBalanceException(string body)
    {
        var (reader, _) = Create(new StubHandler(_ => Rpc(body)));

        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Cronos, Wallet, null, 18, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnconfiguredChain_IsReportedAsError()
    {
        var (reader, handler) = Create(new StubHandler(_ => Result("0x0")));

        Assert.False(reader.Supports(Chain.Bitcoin));
        Assert.True(reader.Supports(Chain.Cronos));
        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            reader.GetBalanceAsync(Chain.Bitcoin, Wallet, null, 8, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }
}
