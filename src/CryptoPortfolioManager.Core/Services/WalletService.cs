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
        var (clean, error) = await ValidateAsync(id, input, ct);
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
        var clean = input with
        {
            Address = input.Address?.Trim() ?? "", Name = input.Name?.Trim() ?? "", Source = input.Source?.Trim() ?? ""
        };

        string? error = null;
        if (clean.Name.Length == 0) error = "Name is required.";
        else if (clean.Name.Length > 100) error = "Name can be at most 100 characters.";
        else if (clean.Source.Length == 0) error = "Source is required.";
        else if (clean.Source.Length > 100) error = "Source can be at most 100 characters.";
        else if (clean.Address.Length == 0) error = "Wallet address is required.";
        else if (clean.Address.Length > 120) error = "Wallet address can be at most 120 characters.";
        // A CEX has no on-chain address, so there any identifier is accepted.
        else if (clean.Type != WalletType.Cex && BlockchainAddress.DetectFamily(clean.Address) is null)
            error = "Wallet address is not recognised. Use an EVM (0x…), Bitcoin (bc1… / 3… / 1…), XRP Ledger (r…), Solana, Sui (0x…, 64 hex), Cardano (addr1… / stake1…) or Stellar (G…) address.";

        if (error is null)
        {
            var existing = await repository.GetWalletsAsync(ct);
            if (existing.Any(w => w.Id != editingId && w.Address.Equals(clean.Address, StringComparison.OrdinalIgnoreCase)))
                error = "A wallet with this address already exists.";
        }

        return (clean, error);
    }
}
