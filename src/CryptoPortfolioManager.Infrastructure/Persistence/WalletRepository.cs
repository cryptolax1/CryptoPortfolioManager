using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

public class WalletRepository(IDbContextFactory<AppDbContext> factory) : IWalletRepository
{
    public async Task<IReadOnlyList<Wallet>> GetWalletsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Wallets.AsNoTracking().OrderBy(w => w.Name).ThenBy(w => w.Id).ToListAsync(ct);
    }

    public async Task<Wallet> AddWalletAsync(Wallet wallet, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        wallet.CreatedAtUtc = DateTime.UtcNow;
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task UpdateWalletAsync(Wallet wallet, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await db.Wallets.Include(w => w.Positions).ThenInclude(p => p.Asset)
            .SingleOrDefaultAsync(w => w.Id == wallet.Id, ct)
            ?? throw new InvalidOperationException($"Wallet {wallet.Id} does not exist.");

        // Positions were read from the old address (or from a wallet that can no longer be scanned): drop them,
        // the next scan recreates what is still there.
        if (!string.Equals(stored.Address, wallet.Address, StringComparison.Ordinal) || wallet.Type == WalletType.Cex)
        {
            RemoveWithLog(db, stored.Positions, "Wallet changed", DateTime.UtcNow);
            stored.LastScannedAtUtc = null;
            stored.LastScanError = null;
        }
        else
        {
            foreach (var position in stored.Positions) position.WalletOrExchangeName = wallet.Name;
        }

        stored.Address = wallet.Address;
        stored.Name = wallet.Name;
        stored.Type = wallet.Type;
        stored.Source = wallet.Source;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteWalletAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.Include(p => p.Asset).Where(p => p.WalletId == id).ToListAsync(ct);
        RemoveWithLog(db, positions, "Wallet deleted", DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        await db.Wallets.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<WhitelistedToken>> GetWhitelistAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WhitelistedTokens.AsNoTracking().Include(t => t.Asset)
            .OrderBy(t => t.Chain).ThenBy(t => t.Symbol).ThenBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<WhitelistedToken> AddWhitelistedTokenAsync(WhitelistedToken token, string coinGeckoId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        token.Asset = await FindOrCreateAssetAsync(db, token, coinGeckoId, ct);
        db.WhitelistedTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task UpdateWhitelistedTokenAsync(WhitelistedToken token, string coinGeckoId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await db.WhitelistedTokens.Include(t => t.Asset).SingleOrDefaultAsync(t => t.Id == token.Id, ct)
            ?? throw new InvalidOperationException($"Whitelisted token {token.Id} does not exist.");

        var existing = await db.Assets.SingleOrDefaultAsync(a => a.CoinGeckoId == coinGeckoId, ct);
        Asset asset;
        if (existing is null)
        {
            // The CoinGecko id is an editable attribute, not the key: rename the asset in place so
            // its positions, price history and other references follow automatically.
            asset = stored.Asset!;
            asset.CoinGeckoId = coinGeckoId;
            asset.Name = token.Name;
            asset.Symbol = token.Symbol;
        }
        else
        {
            asset = existing;
        }

        // Positions read for the old chain or a different (already existing) asset no longer match the whitelist.
        if (stored.Chain != token.Chain || stored.AssetId != asset.Id)
            await RemoveScannedPositionsAsync(db, stored, ct);

        stored.Name = token.Name;
        stored.Symbol = token.Symbol;
        stored.Chain = token.Chain;
        stored.ContractAddress = token.ContractAddress;
        stored.Decimals = token.Decimals;
        var oldAssetId = stored.AssetId;
        stored.Asset = asset;
        await db.SaveChangesAsync(ct);

        // Only when the token moved to another existing asset: drop the orphaned old one.
        if (oldAssetId != asset.Id
            && !await db.WhitelistedTokens.AnyAsync(t => t.AssetId == oldAssetId, ct)
            && !await db.Positions.AnyAsync(p => p.AssetId == oldAssetId, ct))
        {
            await db.Assets.Where(a => a.Id == oldAssetId).ExecuteDeleteAsync(ct);
        }
    }

    public async Task DeleteWhitelistedTokenAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await db.WhitelistedTokens.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (stored is null) return;

        await RemoveScannedPositionsAsync(db, stored, ct);
        db.WhitelistedTokens.Remove(stored);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PositionChanges> ApplyScanAsync(
        int walletId, IReadOnlyCollection<TokenBalance> balances, string? error, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.Id == walletId, ct);
        if (wallet is null) return new PositionChanges(0, 0, 0); // deleted while the scan was running

        var tokens = await db.WhitelistedTokens.AsNoTracking().ToListAsync(ct);
        var positions = await db.Positions.Include(p => p.Asset)
            .Where(p => p.WalletId == walletId || p.WalletAddress == wallet.Address)
            .ToListAsync(ct);

        int added = 0, updated = 0, removed = 0;
        const string reason = "Wallet scan";

        foreach (var balance in balances)
        {
            var token = tokens.FirstOrDefault(t => t.Id == balance.WhitelistedTokenId);
            if (token is null) continue;

            var position = positions.FirstOrDefault(p =>
                p.AssetId == token.AssetId && p.Chain == token.Chain && p.WalletAddress == wallet.Address);

            if (balance.Quantity > 0)
            {
                if (position is null)
                {
                    var asset = await db.Assets.FindAsync([token.AssetId], ct);
                    var created = new Position
                    {
                        AssetId = token.AssetId, Asset = asset, WalletOrExchangeName = wallet.Name,
                        SourceType = PositionSourceType.OnChainWallet, Chain = token.Chain, WalletAddress = wallet.Address,
                        Quantity = balance.Quantity, WalletId = wallet.Id,
                        LastSyncedAtUtc = nowUtc, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
                    };
                    db.Positions.Add(created);
                    LogChange(db, created, PositionChangeType.Added, null, balance.Quantity, reason, nowUtc);
                    added++;
                }
                else
                {
                    if (position.Quantity != balance.Quantity)
                    {
                        LogChange(db, position, PositionChangeType.Updated, position.Quantity, balance.Quantity, reason, nowUtc);
                        updated++;
                    }
                    position.Quantity = balance.Quantity;
                    position.WalletId = wallet.Id;
                    position.SourceType = PositionSourceType.OnChainWallet;
                    position.WalletOrExchangeName = wallet.Name;
                    position.LastSyncedAtUtc = nowUtc;
                    position.UpdatedAtUtc = nowUtc;
                }
            }
            else if (position is { WalletId: not null } && position.WalletId == walletId)
            {
                LogChange(db, position, PositionChangeType.Removed, position.Quantity, null, reason, nowUtc);
                db.Positions.Remove(position);
                removed++;
            }
        }

        foreach (var stale in positions.Where(p =>
                     p.WalletId == walletId && !tokens.Any(t => t.AssetId == p.AssetId && t.Chain == p.Chain)).ToList())
        {
            LogChange(db, stale, PositionChangeType.Removed, stale.Quantity, null, "Token no longer whitelisted", nowUtc);
            db.Positions.Remove(stale);
            removed++;
        }

        wallet.LastScanError = error;
        if (error is null) wallet.LastScannedAtUtc = nowUtc;

        await db.SaveChangesAsync(ct);
        return new PositionChanges(added, updated, removed);
    }

    private static async Task RemoveScannedPositionsAsync(AppDbContext db, WhitelistedToken token, CancellationToken ct)
    {
        var positions = await db.Positions.Include(p => p.Asset)
            .Where(p => p.WalletId != null && p.AssetId == token.AssetId && p.Chain == token.Chain)
            .ToListAsync(ct);
        RemoveWithLog(db, positions, "Token removed from whitelist", DateTime.UtcNow);
    }

    /// <summary>Marks the positions for deletion and logs each removal; the caller saves.</summary>
    private static void RemoveWithLog(AppDbContext db, IEnumerable<Position> positions, string reason, DateTime nowUtc)
    {
        foreach (var position in positions.ToList())
        {
            LogChange(db, position, PositionChangeType.Removed, position.Quantity, null, reason, nowUtc);
            db.Positions.Remove(position);
        }
    }

    private static void LogChange(
        AppDbContext db, Position position, PositionChangeType type, decimal? previous, decimal? now, string reason, DateTime nowUtc) =>
        db.PositionChangeLogs.Add(new PositionChangeLog
        {
            OccurredAtUtc = nowUtc, ChangeType = type, Reason = reason,
            AssetId = position.AssetId, AssetSymbol = position.Asset?.Symbol ?? "?", AssetName = position.Asset?.Name ?? "?",
            WalletOrExchangeName = position.WalletOrExchangeName, Chain = position.Chain, WalletAddress = position.WalletAddress,
            PreviousQuantity = previous, NewQuantity = now
        });

    public async Task<int> RemoveUnmatchedPositionsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.Include(p => p.Asset).Where(p => p.WalletId == null).ToListAsync(ct);
        RemoveWithLog(db, positions, "Not found by wallet scan", nowUtc);
        await db.SaveChangesAsync(ct);
        return positions.Count;
    }

    public async Task<IReadOnlyList<Position>> GetWalletPositionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.AsNoTracking().Include(p => p.Asset)
            .Where(p => p.WalletId != null).ToListAsync(ct);
        return positions.OrderBy(p => p.Asset!.Symbol).ThenBy(p => p.Chain).ThenBy(p => p.Id).ToList();
    }

    public async Task<IReadOnlyList<PositionChangeLog>> GetChangeLogAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Timestamps are ISO text in SQLite, so ordering by Id (insert order) is the reliable newest-first.
        return await db.PositionChangeLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(take).ToListAsync(ct);
    }

    private static async Task<Asset> FindOrCreateAssetAsync(AppDbContext db, WhitelistedToken token, string coinGeckoId, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.CoinGeckoId == coinGeckoId, ct);
        if (asset is not null) return asset;

        asset = new Asset { Name = token.Name, Symbol = token.Symbol, CoinGeckoId = coinGeckoId };
        db.Assets.Add(asset);
        return asset;
    }
}
