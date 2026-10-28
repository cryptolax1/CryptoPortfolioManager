using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public interface IWalletService
{
    Task<IReadOnlyList<Wallet>> GetWalletsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetWalletPositionsAsync(CancellationToken ct = default);
    Task<ServiceResult> AddAsync(WalletInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, WalletInput input, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class WalletService(IWalletRepository repository) : IWalletService
{
    public Task<IReadOnlyList<Wallet>> GetWalletsAsync(CancellationToken ct = default) => repository.GetWalletsAsync(ct);

    public Task<IReadOnlyList<Position>> GetWalletPositionsAsync(CancellationToken ct = default) => repository.GetWalletPositionsAsync(ct);

    public async Task<ServiceResult> AddAsync(WalletInput input, CancellationToken ct = default)
    {
        var (clean, error) = await ValidateAsync(null, input, ct);
        if (error is not null) return ServiceResult.Fail(error);

        await repository.AddWalletAsync(new Wallet
        {
            Address = clean.Address, Name = clean.Name, Type = clean.Type, Source = clean.Source
        }, ct);
        return ServiceResult.Success;
    }

    public async Task<ServiceResult> UpdateAsync(int id, WalletInput input, CancellationToken ct = default)
    {
        var stored = (await repository.GetWalletsAsync(ct)).SingleOrDefault(w => w.Id == id);
        if (stored is null) return ServiceResult.Fail("This wallet no longer exists.");

        // The type is chosen once, when the wallet is added, and cannot change afterwards: an on-chain wallet and an
        // exchange account are read in completely different ways (address vs. API), so switching would silently
        // rewrite where every position comes from. To change the type, add a new wallet.
        var (clean, error) = await ValidateAsync(id, input with { Type = stored.Type }, ct);
        if (error is not null) return ServiceResult.Fail(error);

        await repository.UpdateWalletAsync(new Wallet
        {
            Id = id, Address = clean.Address, Name = clean.Name, Type = clean.Type, Source = clean.Source
        }, ct);
        return ServiceResult.Success;
    }

    public Task DeleteAsync(int id, CancellationToken ct = default) => repository.DeleteWalletAsync(id, ct);

    private async Task<(WalletInput Clean, string? Error)> ValidateAsync(int? editingId, WalletInput input, CancellationToken ct)
    {
        // A CEX has no address at all: its balances come from the exchange API named in Source. Clearing it here (as
        // well as in the form) keeps a wallet that was switched to CEX free of a stale address.
        var clean = input with
        {
            Address = input.Type == WalletType.Cex ? "" : input.Address?.Trim() ?? "",
            Name = input.Name?.Trim() ?? "",
            Source = input.Source?.Trim() ?? ""
        };

        string? error = null;
        if (clean.Name.Length == 0) error = "Name is required.";
        else if (clean.Name.Length > 100) error = "Name can be at most 100 characters.";
        else if (clean.Source.Length == 0) error = "Source is required.";
        else if (clean.Source.Length > 100) error = "Source can be at most 100 characters.";
        else if (clean.Type == WalletType.Cex && ExchangeCatalog.Match(clean.Source) is null)
            error = $"A CEX is read through the exchange's API, so the source must be one of: {string.Join(", ", ExchangeCatalog.All)}.";
        else if (clean.Type != WalletType.Cex && clean.Address.Length == 0) error = "Wallet address is required.";
        else if (clean.Address.Length > 120) error = "Wallet address can be at most 120 characters.";
        else if (clean.Type != WalletType.Cex && BlockchainAddress.DetectFamily(clean.Address) is null)
            error = "Wallet address is not recognised. Use an EVM (0x…), Bitcoin (bc1… / 3… / 1…), XRP Ledger (r…), Solana, Sui (0x…, 64 hex), Cardano (addr1… / stake1…) or Stellar (G…) address.";

        if (error is null && clean.Address.Length > 0)
        {
            var existing = await repository.GetWalletsAsync(ct);
            if (existing.Any(w => w.Id != editingId && w.Address.Equals(clean.Address, StringComparison.OrdinalIgnoreCase)))
                error = "A wallet with this address already exists.";
        }

        if (error is null && clean.Type == WalletType.Cex)
        {
            // One wallet per exchange account: the API credentials name the account, so a second wallet for the same
            // exchange would read exactly the same balances.
            var existing = await repository.GetWalletsAsync(ct);
            var exchange = ExchangeCatalog.Match(clean.Source);
            if (existing.Any(w => w.Id != editingId && w.Type == WalletType.Cex &&
                                  string.Equals(ExchangeCatalog.Match(w.Source), exchange, StringComparison.OrdinalIgnoreCase)))
                error = $"There is already a {exchange} wallet; exchange balances are read per account, so one wallet per exchange is enough.";
        }

        return (clean, error);
    }
}

