using System.Net;
using System.Text;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Blockchain;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Tests;

public class BitcoinSuiCardanoStellarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BtcSegwit = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    private const string BtcTaproot = "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0";
    private const string XlmAccount = "GAAZI4TCR3TY5OJHCTJC2A4QSY6CJWJH5IAJTGKIN2ER7LBNVKOCCWN7";
    private const string XlmIssuer = "GA5ZSEJYB37JRC5AVCIA5MOP4RHTM335X2KGX3IHOJAPP5RE34K4KZVN";
    private const string AdaAddress = "addr1qxqs59lphg8g6qndelq8xwqn60ag3aeyfcp33c2kdp46a09re5df3pzwwmyq946axfcejy5n4x0y99wqpgtp2gd0k09qsgy6pz";
    private const string AdaStake = "stake1uyqqztgeuc50ze7pgq243ggvu42qhv23xr7udapxdwt7zag4szp0r";
    private const string Policy = "29d222ce763455e3d7a09a665ce554f00ac89d2e99a1a83d267170c6";
    private static readonly string SuiWallet = "0x" + new string('0', 63) + "1";

    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return responses[Math.Min(_next++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static IOptions<BlockchainRpcOptions> Opts() =>
        Options.Create(new BlockchainRpcOptions { MaxRetries = 1, RetryBaseDelay = TimeSpan.Zero });

    // ---- Address validation ----

    [Theory]
    [InlineData(BtcSegwit, ChainFamily.Bitcoin)]
    [InlineData("349XFrtCu2LwcWdgftfkPcHtMT2rpQ7cmW", ChainFamily.Bitcoin)] // nested SegWit (P2SH)
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2", ChainFamily.Bitcoin)] // legacy P2PKH
    [InlineData(BtcTaproot, ChainFamily.Bitcoin)]
    [InlineData("BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4", ChainFamily.Bitcoin)]
    [InlineData(XlmAccount, ChainFamily.Stellar)]
    [InlineData(AdaAddress, ChainFamily.Cardano)]
    [InlineData(AdaStake, ChainFamily.Cardano)]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000001", ChainFamily.Sui)]
    public void DetectFamily_RecognisesNewFormats(string address, ChainFamily expected) =>
        Assert.Equal(expected, BlockchainAddress.DetectFamily(address));

    [Theory]
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5")] // checksum
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kV8f3t4")] // mixed case
    [InlineData("349XFrtCu2LwcWdgftfkPcHtMT2rpQ7cmX")] // checksum of a 3… address
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN3")] // checksum of a 1… address
    [InlineData("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx")] // testnet
    [InlineData("GAAZI4TCR3TY5OJHCTJC2A4QSY6CJWJH5IAJTGKIN2ER7LBNVKOCCWN6")] // checksum
    [InlineData("addr1qxqs59lphg8g6qndelq8xwqn60ag3aeyfcp33c2kdp46a09re5df3pzwwmyq946axfcejy5n4x0y99wqpgtp2gd0k09qsgy6pq")] // checksum
    [InlineData("addr_test1vz2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzer5jnd35c")] // testnet
    [InlineData("0x00000000000000000000000000000000000000000000000000000000000001")] // 31 bytes
    public void DetectFamily_RejectsInvalidNewFormats(string address) => Assert.Null(BlockchainAddress.DetectFamily(address));

    [Fact]
    public void TokenAddresses_AreValidatedForNewChains()
    {
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Sui, "0xdba34672e30cb065b1f93e3ab55318768fd6fef66c15942c9f7cb846e2f900e7::usdc::USDC"));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Cardano, Policy + ".4d494e"));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Cardano, Policy));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Stellar, $"USDC:{XlmIssuer}"));
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Bitcoin, null));

        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Sui, "0x2"));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Cardano, Policy + ".4d4"));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Stellar, XlmIssuer));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Bitcoin, "anything"));
    }

    // ---- Bitcoin ----

    [Fact]
    public async Task Bitcoin_Balance_IsFundedMinusSpent()
    {
        var handler = new StubHandler(_ => Json("""{"chain_stats":{"funded_txo_sum":1679761507,"spent_txo_sum":1287005839}}"""));
        var reader = new BitcoinBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(3.92755668m, await reader.GetBalanceAsync(Chain.Bitcoin, BtcSegwit, null, 8, Ct));
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal($"https://mempool.space/api/address/{BtcSegwit}", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Bitcoin_RejectsTokensAndForeignAddresses()
    {
        var reader = new BitcoinBalanceReader(new HttpClient(new StubHandler(_ => Json("{}"))), Opts());

        await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Bitcoin, BtcSegwit, "x", 8, Ct));
        await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Bitcoin, XlmAccount, null, 8, Ct));
        await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Bitcoin, BtcSegwit, null, 8, Ct));
    }

    [Fact]
    public async Task Bitcoin_ServerError_IsRetriedThenReported()
    {
        var handler = new StubHandler(_ => Json("", HttpStatusCode.ServiceUnavailable));
        var reader = new BitcoinBalanceReader(new HttpClient(handler), Opts());

        await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Bitcoin, BtcSegwit, null, 8, Ct));
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- Stellar ----

    private const string StellarBody = $$"""
        {"balances":[
          {"asset_type":"credit_alphanum4","asset_code":"USDC","asset_issuer":"{{XlmIssuer}}","balance":"12.5000000"},
          {"asset_type":"credit_alphanum4","asset_code":"USDC","asset_issuer":"GAAZI4TCR3TY5OJHCTJC2A4QSY6CJWJH5IAJTGKIN2ER7LBNVKOCCWN7","balance":"99.0000000"},
          {"asset_type":"native","balance":"250.1234567"}]}
        """;

    [Fact]
    public async Task Stellar_NativeAndIssuedBalances()
    {
        var reader = new StellarBalanceReader(new HttpClient(new StubHandler(_ => Json(StellarBody))), Opts());

        Assert.Equal(250.1234567m, await reader.GetBalanceAsync(Chain.Stellar, XlmAccount, null, 7, Ct));
        Assert.Equal(12.5m, await reader.GetBalanceAsync(Chain.Stellar, XlmAccount, $"USDC:{XlmIssuer}", 7, Ct));
        Assert.Equal(0m, await reader.GetBalanceAsync(Chain.Stellar, XlmAccount, $"EURC:{XlmIssuer}", 7, Ct));
    }

    [Fact]
    public async Task Stellar_UnfundedAccount_IsZeroButOtherErrorsAreNot()
    {
        var missing = new StellarBalanceReader(new HttpClient(new StubHandler(_ => Json("{}", HttpStatusCode.NotFound))), Opts());
        Assert.Equal(0m, await missing.GetBalanceAsync(Chain.Stellar, XlmAccount, null, 7, Ct));

        var denied = new StellarBalanceReader(new HttpClient(new StubHandler(_ => Json("{}", HttpStatusCode.BadRequest))), Opts());
        await Assert.ThrowsAsync<WalletBalanceException>(() => denied.GetBalanceAsync(Chain.Stellar, XlmAccount, null, 7, Ct));
    }

    // ---- Sui ----

    [Fact]
    public async Task Sui_NativeBalance_UsesGraphQlAndNineDecimals()
    {
        var handler = new StubHandler(_ => Json("""{"data":{"address":{"balance":{"totalBalance":"9832255133"}}}}"""));
        var reader = new SuiBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(9.832255133m, await reader.GetBalanceAsync(Chain.Sui, SuiWallet, null, 9, Ct));
        Assert.Contains("0x2::sui::SUI", handler.Requests[0].Body);
        Assert.Contains(SuiWallet, handler.Requests[0].Body);
    }

    [Fact]
    public async Task Sui_Token_UsesWhitelistDecimals_AndMissingBalanceIsZero()
    {
        const string coin = "0xdba34672e30cb065b1f93e3ab55318768fd6fef66c15942c9f7cb846e2f900e7::usdc::USDC";
        var reader = new SuiBalanceReader(new HttpClient(new StubHandler(_ => Json("""{"data":{"address":{"balance":{"totalBalance":"1500000"}}}}"""))), Opts());
        Assert.Equal(1.5m, await reader.GetBalanceAsync(Chain.Sui, SuiWallet, coin, 6, Ct));

        var empty = new SuiBalanceReader(new HttpClient(new StubHandler(_ => Json("""{"data":{"address":{"balance":null}}}"""))), Opts());
        Assert.Equal(0m, await empty.GetBalanceAsync(Chain.Sui, SuiWallet, coin, 6, Ct));
    }

    [Fact]
    public async Task Sui_GraphQlErrors_AreReported()
    {
                        var reader = new SuiBalanceReader(new HttpClient(new StubHandler(_ => Json("""{"errors":[{"message":"boom"}]}"""))), Opts());

        var ex = await Assert.ThrowsAsync<WalletBalanceException>(() => reader.GetBalanceAsync(Chain.Sui, SuiWallet, null, 9, Ct));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Sui_PostBuffersBodyWithContentLength_NotChunkedTransfer()
    {
        // The Sui GraphQL gateway answers HTTP 400 ("Header of type content-length was missing") to a chunked POST
        // without Content-Length. PostAsJsonAsync on a JsonObject streams chunked content, so the transport must
        // buffer the body instead.
        HttpRequestMessage? seen = null;
        var handler = new InspectingHandler(request =>
        {
            seen = request;
            return Json("""{"data":{"address":{"balance":{"totalBalance":"1000000000"}}}}""");
        });
        var reader = new SuiBalanceReader(new HttpClient(handler), Opts());

                Assert.Equal(1m, await reader.GetBalanceAsync(Chain.Sui, SuiWallet, null, 9, Ct));

        Assert.NotNull(seen);
        Assert.True(seen!.Headers.TransferEncodingChunked != true, "the request must not be sent with chunked transfer encoding");
        Assert.NotNull(seen.Content!.Headers.ContentLength);
        Assert.True(seen.Content.Headers.ContentLength > 0, "the request body must carry a Content-Length");
        Assert.Equal("application/json", seen.Content.Headers.ContentType!.MediaType);
    }

    private sealed class InspectingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // ---- Cardano ----

    [Fact]
    public async Task Cardano_PaymentAddress_NativeBalanceAndAssets()
    {
        var handler = new StubHandler(
            _ => Json($$"""[{"balance":"5882883445","utxo_set":[]}]"""),
            _ => Json($$"""
                [{"balance":"1","utxo_set":[
                  {"asset_list":[{"policy_id":"{{Policy}}","asset_name":"4d494e","quantity":"2500000"}]},
                  {"asset_list":[{"policy_id":"{{Policy}}","asset_name":"4d494e","quantity":"500000"},{"policy_id":"{{Policy}}","asset_name":"","quantity":"7"}]}]}]
                """));
        var reader = new CardanoBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(5882.883445m, await reader.GetBalanceAsync(Chain.Cardano, AdaAddress, null, 6, Ct));
        Assert.Equal(3m, await reader.GetBalanceAsync(Chain.Cardano, AdaAddress, Policy + ".4d494e", 6, Ct));
        Assert.Equal("https://api.koios.rest/api/v1/address_info", handler.Requests[0].Uri.ToString());
        Assert.Contains(AdaAddress, handler.Requests[0].Body);
    }

    [Fact]
    public async Task Cardano_StakeAddress_UsesAccountEndpoints()
    {
        var handler = new StubHandler(
            _ => Json("""[{"total_balance":"6734932445"}]"""),
            _ => Json($$"""[{"asset_list":[{"policy_id":"{{Policy}}","asset_name":"","quantity":"42"}]}]"""));
        var reader = new CardanoBalanceReader(new HttpClient(handler), Opts());

        Assert.Equal(6734.932445m, await reader.GetBalanceAsync(Chain.Cardano, AdaStake, null, 6, Ct));
        Assert.Equal(42m, await reader.GetBalanceAsync(Chain.Cardano, AdaStake, Policy, 0, Ct));
        Assert.EndsWith("/account_info", handler.Requests[0].Uri.ToString());
        Assert.EndsWith("/account_assets", handler.Requests[1].Uri.ToString());
        Assert.Contains("_stake_addresses", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Cardano_UnknownAddress_IsZeroAndGarbageIsAnError()
    {
        var unknown = new CardanoBalanceReader(new HttpClient(new StubHandler(_ => Json("[]"))), Opts());
        Assert.Equal(0m, await unknown.GetBalanceAsync(Chain.Cardano, AdaAddress, null, 6, Ct));
        Assert.Equal(0m, await unknown.GetBalanceAsync(Chain.Cardano, AdaAddress, Policy, 0, Ct));

        var garbage = new CardanoBalanceReader(new HttpClient(new StubHandler(_ => Json("""{"x":1}"""))), Opts());
        await Assert.ThrowsAsync<WalletBalanceException>(() => garbage.GetBalanceAsync(Chain.Cardano, AdaAddress, null, 6, Ct));
    }

    // ---- Wallet service ----

    [Theory]
    [InlineData(BtcSegwit)]
    [InlineData("349XFrtCu2LwcWdgftfkPcHtMT2rpQ7cmW")]
    [InlineData(BtcTaproot)]
    [InlineData(AdaAddress)] // 103 characters
    [InlineData(AdaStake)]
    [InlineData(XlmAccount)]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000001")]
    public async Task WalletService_AcceptsTheNewAddressFormats(string address)
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new WalletService(new WalletRepository(db.CreateFactory()));

        var result = await service.AddAsync(new WalletInput(address, "Cold", WalletType.Cold, "Ledger"), Ct);

        Assert.True(result.Ok, result.Error);
    }

    // ---- Routing ----

    [Fact]
    public void MultiChainReader_RoutesNewChains()
    {
        var http = new HttpClient(new StubHandler(_ => Json("{}")));
        var o = Opts();
        var reader = new MultiChainBalanceReader(new EvmJsonRpcBalanceReader(http, o), new XrpLedgerBalanceReader(http, o), new SolanaBalanceReader(http, o),
            new BitcoinBalanceReader(http, o), new SuiBalanceReader(http, o), new CardanoBalanceReader(http, o), new StellarBalanceReader(http, o));

        Assert.All([Chain.Bitcoin, Chain.Sui, Chain.Cardano, Chain.Stellar], c => Assert.True(reader.Supports(c)));
    }
}
