using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public interface IWhitelistService
{
    Task<IReadOnlyList<WhitelistedToken>> GetAsync(CancellationToken ct = default);
    Task<ServiceResult> AddAsync(WhitelistedTokenInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, WhitelistedTokenInput input, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class WhitelistService(IWalletRepository repository, IWalletBalanceReader reader) : IWhitelistService
{
    public Task<IReadOnlyList<WhitelistedToken>> GetAsync(CancellationToken ct = default) => repository.GetWhitelistAsync(ct);

    public async Task<ServiceResult> AddAsync(WhitelistedTokenInput input, CancellationToken ct = default)
    {
        var (clean, error) = await ValidateAsync(null, input, ct);
        if (error is not null) return ServiceResult.Fail(error);

        await repository.AddWhitelistedTokenAsync(ToEntity(0, clean), clean.CoinGeckoId, ct);
        return ServiceResult.Success;
    }

    public async Task<ServiceResult> UpdateAsync(int id, WhitelistedTokenInput input, CancellationToken ct = default)
    {
        var (clean, error) = await ValidateAsync(id, input, ct);
        if (error is not null) return ServiceResult.Fail(error);

        await repository.UpdateWhitelistedTokenAsync(ToEntity(id, clean), clean.CoinGeckoId, ct);
        return ServiceResult.Success;
    }

    public Task DeleteAsync(int id, CancellationToken ct = default) => repository.DeleteWhitelistedTokenAsync(id, ct);

    private static WhitelistedToken ToEntity(int id, WhitelistedTokenInput i) => new()
    {
        Id = id, Name = i.Name, Symbol = i.Symbol, Chain = i.Chain, ContractAddress = i.ContractAddress, Decimals = i.Decimals
    };

    private async Task<(WhitelistedTokenInput Clean, string? Error)> ValidateAsync(int? editingId, WhitelistedTokenInput input, CancellationToken ct)
    {
        var address = input.ContractAddress?.Trim();
        var clean = input with
        {
            Name = input.Name?.Trim() ?? "",
            Symbol = input.Symbol?.Trim() ?? "",
            CoinGeckoId = input.CoinGeckoId?.Trim().ToLowerInvariant() ?? "",
            ContractAddress = string.IsNullOrEmpty(address) ? null : address
        };

        // An exchange token is matched by its ticker against what the exchange API reports, so it has no address (and
        // no decimals: balances from an exchange are already whole tokens). Decimals still need to be stored, so the
        // default of 18 is kept; it is simply not used for exchange holdings.
        if (clean.Chain == Chain.Exchange && clean.ContractAddress is not null)
            clean = clean with { ContractAddress = null };

                string? error = null;
        if (clean.Name.Length == 0) error = "Token name is required.";
        else if (clean.Name.Length > 100) error = "Token name can be at most 100 characters.";
        else if (clean.Symbol.Length == 0) error = "Token symbol is required.";
        else if (clean.Symbol.Length > 20) error = "Token symbol can be at most 20 characters.";
        // Fiat cash (EUR) is handled automatically by the exchange scan and priced through CoinGecko; it is never a
        // token the user whitelists, so it cannot be added here.
        else if (clean.Chain == Chain.Exchange && FiatCurrencies.IsFiat(clean.Symbol))
            error = $"'{clean.Symbol}' is fiat cash. An exchange wallet shows it automatically and prices it through CoinGecko, so it cannot be whitelisted.";
        else if (clean.CoinGeckoId.Length == 0) error = "Identifier (CoinGecko ID) is required, so the token can be priced.";
        else if (clean.CoinGeckoId.Length > 100) error = "Identifier can be at most 100 characters.";
        // An exchange is not read through a node, so it needs no RPC URL and must not be checked against one.
        else if (clean.Chain != Chain.Exchange && !reader.Supports(clean.Chain))
            error = $"Scanning {ChainInfo.Label(clean.Chain)} is not supported (no node configured).";
        else if (!BlockchainAddress.IsValidTokenAddress(clean.Chain, clean.ContractAddress))
            error = $"Token address must be {BlockchainAddress.TokenAddressHint(clean.Chain)} (leave empty for the native coin).";
        else if (clean.Decimals is < 0 or > 36) error = "Decimals must be between 0 and 36.";

        if (error is null)
        {
            var existing = (await repository.GetWhitelistAsync(ct)).Where(t => t.Id != editingId).ToList();

            // Two entries for one exchange token would be the same position: the balance is matched by ticker only.
            // A missing address does not make a token unique (every exchange token has one); only a real contract does.
            if (clean.Chain == Chain.Exchange && existing.Any(t =>
                    t.Chain == Chain.Exchange && string.Equals(t.Symbol, clean.Symbol, StringComparison.OrdinalIgnoreCase)))
                error = $"'{clean.Symbol}' is already whitelisted as an exchange token, and an exchange balance is matched by ticker, so it can only appear once.";
            else if (clean.ContractAddress is not null && existing.Any(t =>
                         t.Chain == clean.Chain && string.Equals(t.ContractAddress, clean.ContractAddress, StringComparison.OrdinalIgnoreCase)))
                error = $"This token address is already on the whitelist for {ChainInfo.Label(clean.Chain)}.";
            else if (existing.Any(t => t.Chain == clean.Chain && t.ContractAddress is null && clean.ContractAddress is null))
                error = $"The native coin of {ChainInfo.Label(clean.Chain)} is already on the whitelist.";
            else if (existing.Any(t =>
                         t.Chain == clean.Chain && string.Equals(t.Asset?.CoinGeckoId, clean.CoinGeckoId, StringComparison.OrdinalIgnoreCase)))
                error = $"'{clean.CoinGeckoId}' is already whitelisted on {ChainInfo.Label(clean.Chain)}; a wallet can hold only one position per asset and chain.";
        }

        return (clean, error);
    }
}
