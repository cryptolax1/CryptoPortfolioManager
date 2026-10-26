using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads Sui balances through the GraphQL API (<c>address.balance(coinType)</c>), because the public JSON-RPC is
/// deprecated. An empty token address means SUI (<c>0x2::sui::SUI</c>); other coins are written as their coin type.
/// </summary>
public class SuiBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private const string NativeCoinType = "0x2::sui::SUI";
    private const int MistDecimals = 9;
    private const string Query = "query($owner: SuiAddress!, $coinType: String!) { address(address: $owner) { balance(coinType: $coinType) { totalBalance } } }";

    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.Sui && _options.RpcUrls.ContainsKey(nameof(Chain.Sui));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.Sui) throw new WalletBalanceException($"{chain} is not Sui.");
        if (!BlockchainAddress.IsSuiAddress(walletAddress))
            throw new WalletBalanceException("The wallet address is not a Sui address.");
        if (contractAddress is not null && !BlockchainAddress.IsSuiCoinType(contractAddress))
            throw new WalletBalanceException("The token address must be a Sui coin type (0xPACKAGE::module::NAME).");

        var request = new JsonObject
        {
            ["query"] = Query,
            ["variables"] = new JsonObject { ["owner"] = walletAddress, ["coinType"] = contractAddress ?? NativeCoinType }
        };

        var body = await _transport.PostAsync(JsonRpcTransport.Endpoint(_options, chain), request,
            b => b["errors"] is JsonArray { Count: > 0 } errors && JsonRpcTransport.LooksRateLimited(errors[0]?["message"]?.GetValue<string>())
                ? $"The node returned an error: {errors[0]!["message"]!.GetValue<string>()}"
                : null,
            ct);

        if (body["errors"] is JsonArray { Count: > 0 } failures)
            throw new WalletBalanceException($"The node returned an error: {failures[0]?["message"]?.GetValue<string>() ?? "unknown error"}");

        // No balance object means the address never held this coin: a real, empty answer.
        if (body["data"]?["address"]?["balance"]?["totalBalance"] is not JsonValue total) return 0m;

        return total.TryGetValue<string>(out var text) && BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var raw)
            ? EvmJsonRpcBalanceReader.ToTokens(raw, contractAddress is null ? MistDecimals : decimals)
            : throw new WalletBalanceException("The node returned an unreadable Sui balance.");
    }
}
