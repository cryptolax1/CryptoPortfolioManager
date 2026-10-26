using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads the balance of a Bitcoin address (native SegWit <c>bc1…</c>, nested SegWit <c>3…</c> or legacy <c>1…</c>) from an Esplora-compatible REST API
/// (<c>GET /address/{address}</c>, e.g. mempool.space). The balance is the confirmed funded minus spent outputs.
/// Bitcoin has only its native coin, so a token address is rejected.
/// </summary>
public class BitcoinBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private const int SatoshiDecimals = 8;
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.Bitcoin && _options.RpcUrls.ContainsKey(nameof(Chain.Bitcoin));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.Bitcoin) throw new WalletBalanceException($"{chain} is not Bitcoin.");
        if (contractAddress is not null) throw new WalletBalanceException("Bitcoin has no tokens: leave the token address empty.");
        if (!BlockchainAddress.IsBitcoinAddress(walletAddress))
            throw new WalletBalanceException("The wallet address is not a Bitcoin address.");

        var url = JsonRpcTransport.Combine(JsonRpcTransport.Endpoint(_options, chain), $"address/{walletAddress}");
        var body = await _transport.GetAsync(url, notFoundIsEmpty: true, ct);
        if (body is null) return 0m;

        var stats = body["chain_stats"];
        if (!TryRead(stats?["funded_txo_sum"], out var funded) || !TryRead(stats?["spent_txo_sum"], out var spent) || spent > funded)
            throw new WalletBalanceException("The node returned an unreadable Bitcoin balance.");

        return EvmJsonRpcBalanceReader.ToTokens(funded - spent, SatoshiDecimals);
    }

    private static bool TryRead(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value) && value >= 0;
    }
}
