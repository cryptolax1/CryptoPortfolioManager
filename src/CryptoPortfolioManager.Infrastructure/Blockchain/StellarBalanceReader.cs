using System.Globalization;
using System.Text.Json.Nodes;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>
/// Reads Stellar balances from the Horizon REST API (<c>GET /accounts/{id}</c>): the native balance for XLM and the
/// trustline balances for issued assets. The token address is written as <c>CODE:issuer</c>.
/// </summary>
/// <remarks>XLM balances include the reserve the network locks for the account; it is not subtracted.</remarks>
public class StellarBalanceReader(HttpClient http, IOptions<BlockchainRpcOptions> options) : IWalletBalanceReader
{
    private readonly BlockchainRpcOptions _options = options.Value;
    private readonly JsonRpcTransport _transport = new(http, options.Value);

    public bool Supports(Chain chain) => chain == Chain.Stellar && _options.RpcUrls.ContainsKey(nameof(Chain.Stellar));

    public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
    {
        if (chain != Chain.Stellar) throw new WalletBalanceException($"{chain} is not Stellar.");
        if (!BlockchainAddress.IsStellarAccount(walletAddress))
            throw new WalletBalanceException("The wallet address is not a Stellar address.");

        string? code = null, issuer = null;
        if (contractAddress is not null && !BlockchainAddress.TryParseStellarAsset(contractAddress, out code, out issuer))
            throw new WalletBalanceException("The token address must be written as CODE:issuer.");

        var url = JsonRpcTransport.Combine(JsonRpcTransport.Endpoint(_options, chain), $"accounts/{walletAddress}");
        var body = await _transport.GetAsync(url, notFoundIsEmpty: true, ct);
        if (body is null) return 0m; // the account is not funded yet, so it holds nothing

        decimal total = 0m;
        foreach (var line in body["balances"]?.AsArray() ?? [])
        {
            var type = line?["asset_type"]?.GetValue<string>();
            var matches = contractAddress is null
                ? type == "native"
                : type is "credit_alphanum4" or "credit_alphanum12"
                  && line!["asset_code"]?.GetValue<string>() == code
                  && line["asset_issuer"]?.GetValue<string>() == issuer;
            if (!matches) continue;

            if (!decimal.TryParse(line!["balance"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var balance) || balance < 0)
                throw new WalletBalanceException("The node returned an unreadable Stellar balance.");
            total += balance;
        }

        return total;
    }
}
