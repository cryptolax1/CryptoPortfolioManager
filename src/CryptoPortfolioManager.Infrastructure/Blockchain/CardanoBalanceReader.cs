using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads Cardano balances from the Koios REST API. A payment address (<c>addr1…</c>) is read with <c>address_info</c>;
/// a stake address (<c>stake1…</c>, the whole wallet account including rewards) with <c>account_info</c> and
/// <c>account_assets</c>. A native asset is written as <c>policyId.assetNameHex</c>.
/// </summary>
public class CardanoBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private const int LovelaceDecimals = 6;
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.Cardano && _options.RpcUrls.ContainsKey(nameof(Chain.Cardano));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.Cardano) throw new WalletBalanceException($"{chain} is not Cardano.");
        if (!BlockchainAddress.IsCardanoAddress(walletAddress))
            throw new WalletBalanceException("The wallet address is not a Cardano (addr1… or stake1…) address.");

        string policyId = "", assetName = "";
        if (contractAddress is not null && !BlockchainAddress.TryParseCardanoAsset(contractAddress, out policyId, out assetName))
            throw new WalletBalanceException("The token address must be written as policyId.assetNameHex.");

        var endpoint = JsonRpcTransport.Endpoint(_options, chain);
        var isStake = walletAddress.StartsWith("stake", StringComparison.OrdinalIgnoreCase);

        if (contractAddress is null)
        {
            var info = await QueryAsync(endpoint, isStake ? "account_info" : "address_info", walletAddress, isStake, ct);
            var text = info?[isStake ? "total_balance" : "balance"]?.GetValue<string>();
            if (info is null) return 0m; // an address that never appeared on-chain holds nothing

            return text is not null && BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var lovelace)
                ? EvmJsonRpcBalanceReader.ToTokens(lovelace, LovelaceDecimals)
                : throw new WalletBalanceException("The node returned an unreadable ADA balance.");
        }

        BigInteger total = 0;
        if (isStake)
        {
            total = SumAssets((await QueryAsync(endpoint, "account_assets", walletAddress, isStake, ct))?["asset_list"], policyId, assetName);
        }
        else
        {
            var info = await QueryAsync(endpoint, "address_info", walletAddress, isStake, ct);
            foreach (var utxo in info?["utxo_set"]?.AsArray() ?? [])
                total += SumAssets(utxo?["asset_list"], policyId, assetName);
        }

        return EvmJsonRpcBalanceReader.ToTokens(total, decimals);
    }

    private static BigInteger SumAssets(JsonNode? assetList, string policyId, string assetName)
    {
        BigInteger total = 0;
        foreach (var asset in assetList?.AsArray() ?? [])
        {
            if (asset is null ||
                !string.Equals(asset["policy_id"]?.GetValue<string>(), policyId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(asset["asset_name"]?.GetValue<string>() ?? "", assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (asset["quantity"]?.GetValue<string>() is not { } text ||
                !BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
                throw new WalletBalanceException("The node returned an unreadable token balance.");
            total += quantity;
        }

        return total;
    }

    /// <summary>Returns the first result row, or null when Koios knows nothing about the address (an empty list).</summary>
    private async Task<JsonNode?> QueryAsync(Uri endpoint, string method, string address, bool isStake, CancellationToken ct)
    {
        var request = new JsonObject { [isStake ? "_stake_addresses" : "_addresses"] = new JsonArray(address) };
        var body = await _transport.PostForNodeAsync(JsonRpcTransport.Combine(endpoint, method), request, ct);

        return body is JsonArray rows
            ? rows.Count == 0 ? null : rows[0]
            : throw new WalletBalanceException("The node returned an unexpected response.");
    }
}
