using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Blockchain;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Tests;

public class NonEvmChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string XrpAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    private const string XrpIssuer = "rvYAfWj5gh67oV6fW32ZzP3Aw4Eubs59B";
    private const string SolWallet = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
    private const string SolUsdcMint = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    private const string EvmWallet = "0x59711D16ff7F32EAe184BD1B892C7e6d6C058bFc";

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

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static IOptions<BlockchainRpcOptions> Opts(int maxRetries = 2) =>
        Options.Create(new BlockchainRpcOptions { MaxRetries = maxRetries, RetryBaseDelay = TimeSpan.Zero });

    // ---- Address validation ----

    [Theory]
    [InlineData("0x59711D16ff7F32EAe184BD1B892C7e6d6C058bFc", ChainFamily.Evm)]
    [InlineData(XrpAccount, ChainFamily.XrpLedger)]
    [InlineData(SolWallet, ChainFamily.Solana)]
    [InlineData("11111111111111111111111111111111", ChainFamily.Solana)]
    public void DetectFamily_RecognisesEachFormat(string address, ChainFamily expected) =>
        Assert.Equal(expected, BlockchainAddress.DetectFamily(address));

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTi")] // checksum no longer matches
    [InlineData("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAW")] // too short for a Solana key
    [InlineData("0x1234")]
    [InlineData("bc1qexampleexampleexampleexampleexample0")]
    public void DetectFamily_RejectsInvalidAddresses(string address) => Assert.Null(BlockchainAddress.DetectFamily(address));

    [Fact]
    public void TokenAddresses_AreValidatedPerFamily()
    {
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Scroll, "0x06eFdBFf2a14a7c8E15944D1F4A48F9F95F663A4"));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Solana, SolUsdcMint));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.XrpLedger, $"USD.{XrpIssuer}"));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.XrpLedger, null));

        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Solana, "0x06eFdBFf2a14a7c8E15944D1F4A48F9F95F663A4"));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.XrpLedger, XrpIssuer)); // currency is missing
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.XrpLedger, "USD.rnotvalid"));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Scroll, SolUsdcMint));
    }

    // ---- XRP Ledger ----

    [Fact]
    public async Task Xrp_NativeBalance_ConvertsDropsAndUsesAccountInfo()
    {
        var handler = new StubHandler(_ => Json("""{"result":{"status":"success","account_data":{"Balance":"56786636999"}}}"""));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        var balance = await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, null, 6, Ct);

        Assert.Equal(56786.636999m, balance);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://xrplcluster.com/", request.Uri.ToString());
        Assert.Equal("account_info", request.Body["method"]!.GetValue<string>());
        Assert.Equal(XrpAccount, request.Body["params"]![0]!["account"]!.GetValue<string>());
    }

    [Fact]
    public async Task Xrp_UnfundedAccount_IsAnEmptyBalance_NotAnError()
    {
        var handler = new StubHandler(_ => Json("""{"result":{"status":"error","error":"actNotFound"}}"""));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(0m, await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, null, 6, Ct));
        Assert.Equal(0m, await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, $"USD.{XrpIssuer}", 15, Ct));
    }

    [Fact]
    public async Task Xrp_IssuedToken_UsesAccountLinesForTheIssuer_AndSumsMatchingCurrency()
    {
        var handler = new StubHandler(_ => Json($$$"""
            {"result":{"status":"success","lines":[
              {"account":"{{{XrpIssuer}}}","currency":"USD","balance":"12.5"},
              {"account":"{{{XrpIssuer}}}","currency":"EUR","balance":"99"}]}}
            """));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        var balance = await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, $"USD.{XrpIssuer}", 15, Ct);

        Assert.Equal(12.5m, balance);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("account_lines", request.Body["method"]!.GetValue<string>());
        Assert.Equal(XrpIssuer, request.Body["params"]![0]!["peer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Xrp_NegativeLineBalance_CountsAsZero()
    {
        var handler = new StubHandler(_ => Json("""{"result":{"status":"success","lines":[{"currency":"USD","balance":"-1000"}]}}"""));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(0m, await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, $"USD.{XrpIssuer}", 15, Ct));
    }

    [Fact]
    public async Task Xrp_NodeError_IsAFailure_NotZero()
    {
        var handler = new StubHandler(_ => Json("""{"result":{"status":"error","error":"invalidParams"}}"""));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        var ex = await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, null, 6, Ct));
        Assert.Contains("invalidParams", ex.Message);
    }

    [Fact]
    public async Task Xrp_SlowDown_IsRetried()
    {
        var handler = new StubHandler(
            _ => Json("""{"result":{"status":"error","error":"slowDown"}}"""),
            _ => Json("""{"result":{"status":"success","account_data":{"Balance":"2000000"}}}"""));
        var reader = new XrpLedgerBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(2m, await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, null, 6, Ct));
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- Solana ----

    [Fact]
    public async Task Solana_NativeBalance_UsesGetBalance()
    {
        var handler = new StubHandler(_ => Json("""{"jsonrpc":"2.0","result":{"context":{"slot":1},"value":2500000000},"id":1}"""));
        var reader = new SolanaBalanceReader(new HttpClient(handler), Opts());

        var balance = await reader.GetBalanceAsync(Chain.Solana, SolWallet, null, 9, Ct);

        Assert.Equal(2.5m, balance);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.mainnet-beta.solana.com/", request.Uri.ToString());
        Assert.Equal("getBalance", request.Body["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Solana_SplToken_SumsAllTokenAccounts_AndUsesChainDecimals()
    {
        var handler = new StubHandler(_ => Json("""
            {"jsonrpc":"2.0","result":{"value":[
              {"account":{"data":{"parsed":{"info":{"tokenAmount":{"amount":"1500000","decimals":6}}}}}},
              {"account":{"data":{"parsed":{"info":{"tokenAmount":{"amount":"2250000","decimals":6}}}}}}]},"id":1}
            """));
        var reader = new SolanaBalanceReader(new HttpClient(handler), Opts());

        // The whitelist says 18 decimals; the chain says 6 and wins.
        var balance = await reader.GetBalanceAsync(Chain.Solana, SolWallet, SolUsdcMint, 18, Ct);

        Assert.Equal(3.75m, balance);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("getTokenAccountsByOwner", request.Body["method"]!.GetValue<string>());
        Assert.Equal(SolUsdcMint, request.Body["params"]![1]!["mint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Solana_NoTokenAccount_IsZero()
    {
        var handler = new StubHandler(_ => Json("""{"jsonrpc":"2.0","result":{"value":[]},"id":1}"""));
        var reader = new SolanaBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(0m, await reader.GetBalanceAsync(Chain.Solana, SolWallet, SolUsdcMint, 6, Ct));
    }

    [Fact]
    public async Task Solana_RpcError_IsAFailure_AndRateLimitIsRetried()
    {
        var failing = new StubHandler(_ => Json("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid param"},"id":1}"""));
        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            new SolanaBalanceReader(new HttpClient(failing), Opts()).GetBalanceAsync(Chain.Solana, SolWallet, null, 9, Ct));
        Assert.Single(failing.Requests);

        var limited = new StubHandler(
            _ => Json("{}", HttpStatusCode.TooManyRequests),
            _ => Json("""{"jsonrpc":"2.0","result":{"value":1000000000},"id":1}"""));
        Assert.Equal(1m, await new SolanaBalanceReader(new HttpClient(limited), Opts()).GetBalanceAsync(Chain.Solana, SolWallet, null, 9, Ct));
        Assert.Equal(2, limited.Requests.Count);
    }

    [Fact]
    public async Task Readers_RejectWalletsOfAnotherFamily()
    {
        var handler = new StubHandler(_ => Json("{}"));
        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            new SolanaBalanceReader(new HttpClient(handler), Opts()).GetBalanceAsync(Chain.Solana, EvmWallet, null, 9, Ct));
        await Assert.ThrowsAsync<WalletBalanceException>(() =>
            new XrpLedgerBalanceReader(new HttpClient(handler), Opts()).GetBalanceAsync(Chain.XrpLedger, EvmWallet, null, 6, Ct));
        Assert.Empty(handler.Requests);
    }

    // ---- Routing ----

    [Fact]
    public async Task MultiChainReader_RoutesByFamily_AndSupportsTheNewChains()
    {
        var evm = new StubHandler(_ => Json("""{"jsonrpc":"2.0","id":1,"result":"0xde0b6b3a7640000"}"""));
        var xrp = new StubHandler(_ => Json("""{"result":{"status":"success","account_data":{"Balance":"1000000"}}}"""));
        var sol = new StubHandler(_ => Json("""{"jsonrpc":"2.0","result":{"value":3000000000},"id":1}"""));
        var reader = new MultiChainBalanceReader(
            new EvmJsonRpcBalanceReader(new HttpClient(evm), Opts()),
            new XrpLedgerBalanceReader(new HttpClient(xrp), Opts()),
            new SolanaBalanceReader(new HttpClient(sol), Opts()),
            new BitcoinBalanceReader(new HttpClient(sol), Opts()),
            new SuiBalanceReader(new HttpClient(sol), Opts()),
            new CardanoBalanceReader(new HttpClient(sol), Opts()),
            new StellarBalanceReader(new HttpClient(sol), Opts()));

        Assert.All([Chain.Scroll, Chain.XrpLedger, Chain.Solana, Chain.Ethereum, Chain.Bitcoin], c => Assert.True(reader.Supports(c)));

        Assert.Equal(1m, await reader.GetBalanceAsync(Chain.Scroll, EvmWallet, null, 18, Ct));
        Assert.Equal("https://rpc.scroll.io/", Assert.Single(evm.Requests).Uri.ToString());
        Assert.Equal(1m, await reader.GetBalanceAsync(Chain.XrpLedger, XrpAccount, null, 6, Ct));
        Assert.Equal(3m, await reader.GetBalanceAsync(Chain.Solana, SolWallet, null, 9, Ct));
        await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Bitcoin, "x", null, 8, Ct));
    }
}
