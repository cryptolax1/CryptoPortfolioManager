using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads balances over standard EVM JSON-RPC: <c>eth_getBalance</c> for the native coin and
/// <c>eth_call</c> with ERC-20 <c>balanceOf(address)</c> for tokens.
/// </summary>
public class EvmJsonRpcBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private const string BalanceOfSelector = "0x70a08231";
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) =>
        ChainInfo.FamilyOf(chain) == ChainFamily.Evm && _options.RpcUrls.ContainsKey(chain.ToString());

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (ChainInfo.FamilyOf(chain) != ChainFamily.Evm)
            throw new WalletBalanceException($"{chain} is not an EVM chain.");

        var endpoint = JsonRpcTransport.Endpoint(_options, chain);

        var request = contractAddress is null
            ? Request("eth_getBalance", walletAddress, "latest")
            : Request("eth_call",
                new JsonObject { ["to"] = contractAddress, ["data"] = BalanceOfSelector + walletAddress[2..].ToLowerInvariant().PadLeft(64, '0') },
                "latest");

        var hex = await SendAsync(endpoint, request, ct);
        return ToTokens(ParseQuantity(hex, contractAddress is not null), decimals);
    }

    private static JsonObject Request(string method, params object[] parameters) => JsonRpcTransport.Rpc(method, parameters);

    private async Task<string> SendAsync(Uri endpoint, JsonObject request, CancellationToken ct)
    {
        var body = await _transport.PostAsync(endpoint, request,
            b => b["error"] is JsonNode error && JsonRpcTransport.LooksRateLimited(error["message"]?.GetValue<string>())
                ? $"The node returned an error: {error["message"]!.GetValue<string>()}"
                : null,
            ct);

        if (body["error"] is JsonNode failure)
            throw new WalletBalanceException($"The node returned an error: {failure["message"]?.GetValue<string>() ?? "unknown error"}");

        return body["result"] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw new WalletBalanceException("The node response contained no result.");
    }

    private static BigInteger ParseQuantity(string hex, bool isContractCall)
    {
        // "0x" from eth_call means the address is not a contract (or not an ERC-20): that is not a zero balance.
        if (isContractCall && hex is "0x" or "")
            throw new WalletBalanceException("The token address is not a contract that returns a balance.");

        if (!hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !BigInteger.TryParse("0" + hex[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new WalletBalanceException($"The node returned an unreadable balance ('{hex}').");

        return value;
    }

    /// <summary>Raw integer to whole tokens without floating point; precision beyond 28 digits is dropped.</summary>
    public static decimal ToTokens(BigInteger raw, int decimals)
    {
        if (decimals > 28)
        {
            raw /= BigInteger.Pow(10, decimals - 28);
            decimals = 28;
        }

        var divisor = BigInteger.Pow(10, decimals);
        var whole = BigInteger.DivRem(raw, divisor, out var fraction);

        if (whole > (BigInteger)decimal.MaxValue)
            throw new WalletBalanceException("The balance is too large to be represented.");

        var fractionPart = decimals == 0 ? 0m : (decimal)fraction / (decimal)divisor;
        return Math.Round((decimal)whole + fractionPart, 18);
    }
}
