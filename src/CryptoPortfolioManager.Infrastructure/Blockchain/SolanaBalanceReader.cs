using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads Solana balances over JSON-RPC: <c>getBalance</c> for SOL and <c>getTokenAccountsByOwner</c> (filtered on the
/// mint) for SPL tokens. The token decimals come from the chain itself, not from the whitelist.
/// </summary>
public class SolanaBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.Solana && _options.RpcUrls.ContainsKey(nameof(Chain.Solana));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.Solana) throw new WalletBalanceException($"{chain} is not Solana.");
        if (!BlockchainAddress.IsSolanaAddress(walletAddress))
            throw new WalletBalanceException("The wallet address is not a Solana address.");

        var endpoint = JsonRpcTransport.Endpoint(_options, chain);

        if (contractAddress is null)
        {
            var result = await CallAsync(endpoint, JsonRpcTransport.Rpc("getBalance", walletAddress), ct);
            return result["value"] is JsonValue lamports && lamports.TryGetValue<long>(out var raw) && raw >= 0
                ? EvmJsonRpcBalanceReader.ToTokens(raw, decimals)
                : throw new WalletBalanceException("The node returned an unreadable SOL balance.");
        }

        var accounts = await CallAsync(endpoint, JsonRpcTransport.Rpc("getTokenAccountsByOwner", walletAddress,
            new JsonObject { ["mint"] = contractAddress }, new JsonObject { ["encoding"] = "jsonParsed" }), ct);

        // No token account at all is a real answer: the wallet never held this token.
        BigInteger total = 0;
        int? tokenDecimals = null;
        foreach (var account in accounts["value"]?.AsArray() ?? [])
        {
            var amount = account?["account"]?["data"]?["parsed"]?["info"]?["tokenAmount"];
            if (amount?["amount"]?.GetValue<string>() is not { } text ||
                !BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) ||
                amount["decimals"] is not JsonValue d || !d.TryGetValue<int>(out var accountDecimals))
                throw new WalletBalanceException("The node returned an unreadable token balance.");

            total += raw;
            tokenDecimals = accountDecimals;
        }

        return tokenDecimals is null ? 0m : EvmJsonRpcBalanceReader.ToTokens(total, tokenDecimals.Value);
    }

    private async Task<JsonNode> CallAsync(Uri endpoint, JsonObject request, CancellationToken ct)
    {
        var body = await _transport.PostAsync(endpoint, request,
            b => b["error"] is JsonNode error && JsonRpcTransport.LooksRateLimited(error["message"]?.GetValue<string>())
                ? $"The node returned an error: {error["message"]!.GetValue<string>()}"
                : null,
            ct);

        if (body["error"] is JsonNode failure)
            throw new WalletBalanceException($"The node returned an error: {failure["message"]?.GetValue<string>() ?? "unknown error"}");

        return body["result"] ?? throw new WalletBalanceException("The node response contained no result.");
    }
}
