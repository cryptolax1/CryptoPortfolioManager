using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads balances from the XRP Ledger through the rippled JSON-RPC API: <c>account_info</c> for XRP and
/// <c>account_lines</c> (filtered on the issuer) for issued tokens. The contract address is written as <c>CURRENCY.issuer</c>.
/// </summary>
/// <remarks>XRP balances include the reserve the ledger locks for the account; it is not subtracted.</remarks>
public class XrpLedgerBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private const int DropDecimals = 6;
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.XrpLedger && _options.RpcUrls.ContainsKey(nameof(Chain.XrpLedger));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.XrpLedger) throw new WalletBalanceException($"{chain} is not the XRP Ledger.");
        if (!BlockchainAddress.IsXrpLedgerAccount(walletAddress))
            throw new WalletBalanceException("The wallet address is not an XRP Ledger address.");

        var endpoint = JsonRpcTransport.Endpoint(_options, chain);

        if (contractAddress is null)
        {
            var result = await CallAsync(endpoint, "account_info", new JsonObject { ["account"] = walletAddress, ["ledger_index"] = "validated" }, ct);
            if (result is null) return 0m; // the account is not activated yet, so it holds nothing

            var drops = result["account_data"]?["Balance"]?.GetValue<string>();
            return drops is not null && BigInteger.TryParse(drops, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? EvmJsonRpcBalanceReader.ToTokens(value, DropDecimals)
                : throw new WalletBalanceException("The node returned an unreadable XRP balance.");
        }

        if (!BlockchainAddress.TryParseXrpLedgerToken(contractAddress, out var currency, out var issuer))
            throw new WalletBalanceException("The token address must be written as CURRENCY.issuer.");

        var lines = await CallAsync(endpoint, "account_lines",
            new JsonObject { ["account"] = walletAddress, ["peer"] = issuer, ["ledger_index"] = "validated", ["limit"] = 400 }, ct);
        if (lines is null) return 0m;

        decimal total = 0m;
        foreach (var line in lines["lines"]?.AsArray() ?? [])
        {
            if (!string.Equals(line?["currency"]?.GetValue<string>(), currency, StringComparison.OrdinalIgnoreCase)) continue;

            var text = line!["balance"]?.GetValue<string>();
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance))
                throw new WalletBalanceException("The node returned an unreadable token balance.");
            total += balance;
        }

        // A negative line balance means the wallet owes the token (it is the issuer): it holds none.
        return Math.Round(Math.Max(total, 0m), 18);
    }

    /// <summary>Returns the result object, or null when the account does not exist (a valid, empty answer).</summary>
    private async Task<JsonNode?> CallAsync(Uri endpoint, string method, JsonObject parameters, CancellationToken ct)
    {
        var request = new JsonObject { ["method"] = method, ["params"] = new JsonArray(parameters) };
        var body = await _transport.PostAsync(endpoint, request, b => RateLimited(b["result"]), ct);

        var result = body["result"] ?? throw new WalletBalanceException("The node response contained no result.");
        if (result["status"]?.GetValue<string>() == "success") return result;

        var error = result["error"]?.GetValue<string>() ?? "unknown error";
        return error == "actNotFound"
            ? null
            : throw new WalletBalanceException($"The node returned an error: {error}");
    }

    private static string? RateLimited(JsonNode? result) =>
        result?["error"]?.GetValue<string>() is "slowDown" or "tooBusy" or "noNetwork" or "noCurrent" or "noClosed"
            ? $"The node returned an error: {result["error"]!.GetValue<string>()}"
            : null;
}
