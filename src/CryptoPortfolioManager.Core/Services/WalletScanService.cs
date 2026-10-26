using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public interface IWalletScanService
{
    Task<WalletScanResult> ScanAsync(CancellationToken ct = default);

    /// <summary>Re-scans one wallet only; positions of other wallets are left alone.</summary>
    Task<WalletScanResult> ScanWalletAsync(int walletId, CancellationToken ct = default);
}

public sealed class WalletScanLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>
/// Updates the positions from what the wallets hold on-chain, limited to whitelisted tokens. A balance that cannot be
/// read leaves the stored position unchanged: a failed read is never treated as "zero". After an error-free scan,
/// positions that no wallet owns are removed from the current view (and logged).
/// </summary>
public class WalletScanService(
    IWalletRepository repository,
    IWalletBalanceReader reader,
    WalletScanLock scanLock,
    TimeProvider clock) : IWalletScanService
{
    public Task<WalletScanResult> ScanAsync(CancellationToken ct = default) => RunAsync(null, ct);

    public Task<WalletScanResult> ScanWalletAsync(int walletId, CancellationToken ct = default) => RunAsync(walletId, ct);

    private async Task<WalletScanResult> RunAsync(int? onlyWalletId, CancellationToken ct)
    {
        if (!await scanLock.Semaphore.WaitAsync(0, ct)) return WalletScanResult.Busy;

        try
        {
            var wallets = await repository.GetWalletsAsync(ct);
            if (onlyWalletId is { } id)
            {
                wallets = wallets.Where(w => w.Id == id).ToList();
                if (wallets.Count == 0)
                    return new WalletScanResult(false, 0, 0, new PositionChanges(0, 0, 0), ["The wallet does not exist (anymore)."]);
            }

            var tokens = await repository.GetWhitelistAsync(ct);

            var errors = new List<string>();
            int scanned = 0, skipped = 0, added = 0, updated = 0, removed = 0;

            foreach (var wallet in wallets)
            {
                if (wallet.Type == WalletType.Cex)
                {
                    skipped++;
                    continue;
                }

                var family = BlockchainAddress.DetectFamily(wallet.Address);
                var walletTokens = family is null ? [] : tokens.Where(t => ChainInfo.FamilyOf(t.Chain) == family).ToList();
                var balances = new List<TokenBalance>();
                var walletErrors = new List<string>();

                if (family is null)
                    walletErrors.Add($"{wallet.Name}: the address format is not recognised, so it cannot be scanned.");

                // An address only exists on chains of its own family (an EVM address is not an XRP Ledger account).
                foreach (var token in walletTokens)
                {
                    try
                    {
                        var quantity = await reader.GetBalanceAsync(token.Chain, wallet.Address, token.ContractAddress, token.Decimals, ct);
                        balances.Add(new TokenBalance(token.Id, quantity));
                    }
                    catch (WalletBalanceException ex)
                    {
                        walletErrors.Add($"{wallet.Name}: {token.Symbol} on {token.Chain}: {ex.Message}");
                    }
                }

                var changes = await repository.ApplyScanAsync(
                    wallet.Id, balances, walletErrors.Count == 0 ? null : string.Join(Environment.NewLine, walletErrors),
                    clock.GetUtcNow().UtcDateTime, ct);

                scanned++;
                added += changes.Added;
                updated += changes.Updated;
                removed += changes.Removed;
                errors.AddRange(walletErrors);
            }

            // The scan is leading: only after a clean scan do positions no wallet owns leave the current view.
            // They stay in the change log, so earlier holdings remain traceable.
            // A single-wallet scan never prunes: other wallets were not re-read.
            if (onlyWalletId is null && scanned > 0 && errors.Count == 0)
                removed += await repository.RemoveUnmatchedPositionsAsync(clock.GetUtcNow().UtcDateTime, ct);

            return new WalletScanResult(false, scanned, skipped, new PositionChanges(added, updated, removed), errors);
        }
        finally
        {
            scanLock.Semaphore.Release();
        }
    }
}
