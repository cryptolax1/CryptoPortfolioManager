using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
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

                // Positions were read with other settings (an old address, another exchange, or the wallet can no longer be
        // read at all) and no longer describe the wallet: drop them, the next scan recreates what is still there.
        // Changing only the name keeps them (renamed below).
        var addressChanged = !string.Equals(stored.Address, wallet.Address, StringComparison.Ordinal);
        var sourceChanged = !string.Equals(stored.Source, wallet.Source, StringComparison.OrdinalIgnoreCase);
        var noLongerScannable = !IsScannable(wallet);
        if (addressChanged || (sourceChanged && wallet.Type == WalletType.Cex) || noLongerScannable)
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
        var tokens = await db.WhitelistedTokens.AsNoTracking().Include(t => t.Asset)
            .OrderBy(t => t.Chain).ThenBy(t => t.Symbol).ThenBy(t => t.Id).ToListAsync(ct);
        // Fiat cash (EUR) is an internal scan mechanic, never a token the user whitelists. A stray row from an older
        // database is hidden here, so it never shows up in the whitelist and never takes part in whitelist validation.
        return tokens.Where(t => !IsFiatToken(t)).ToList();
    }

    /// <summary>
    /// Whether a whitelist entry actually represents fiat cash rather than a token the user chose: its symbol is a
    /// known fiat currency, or its asset was priced as one. Such an entry is never shown or edited on the whitelist.
    /// </summary>
    private static bool IsFiatToken(WhitelistedToken token) =>
        FiatCurrencies.IsFiat(token.Symbol) || FiatCurrencies.IsFiatAssetId(token.Asset?.CoinGeckoId);

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
            //
            // Whitelisting an exchange ticker is how the price-less asset the scan made for it (id "exchange-sei")
            // becomes the real coin: adopt that row instead of leaving an orphan next to the new asset.
            var placeholder = await FindPlaceholderAssetAsync(db, token, ct);
            asset = placeholder is not null && placeholder.Id != stored.AssetId ? placeholder : stored.Asset!;
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
        int walletId, IReadOnlyCollection<TokenBalance> balances, string? error, DateTime nowUtc,
        IReadOnlyCollection<ExchangePosition> exchangeHoldings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.Id == walletId, ct);
        if (wallet is null) return new PositionChanges(0, 0, 0); // deleted while the scan was running

                // One statement per scan: the exchange holdings and the on-chain balances are all saved together at the end of
        // this method, so a half-applied scan cannot be stored.
        var tokens = await db.WhitelistedTokens.AsNoTracking().ToListAsync(ct);
        var positions = await db.Positions.Include(p => p.Asset)
            .Where(p => p.WalletId == walletId || p.WalletAddress == wallet.Address)
            .ToListAsync(ct);

        var exchangeChanges = await ApplyExchangeHoldingsAsync(db, wallet, exchangeHoldings, null, nowUtc, positions, tokens, ct);

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
        return new PositionChanges(added + exchangeChanges.Added, updated + exchangeChanges.Updated, removed + exchangeChanges.Removed);
    }

    public async Task<PositionChanges> ApplyExchangeScanAsync(
        int walletId, IReadOnlyCollection<ExchangePosition> holdings, string? error, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.Id == walletId, ct);
        if (wallet is null) return new PositionChanges(0, 0, 0); // deleted while the scan was running

        var tokens = await db.WhitelistedTokens.Include(t => t.Asset).ToListAsync(ct);
        var positions = await db.Positions.Include(p => p.Asset).Where(p => p.WalletId == walletId).ToListAsync(ct);

                var changes = await ApplyExchangeHoldingsAsync(db, wallet, holdings, error, nowUtc, positions, tokens, ct);

        wallet.LastScanError = error;
        if (error is null) wallet.LastScannedAtUtc = nowUtc;

        await db.SaveChangesAsync(ct);
        return changes;
    }

                /// <summary>
    /// The exchange half of a scan, working on an already-loaded context and wallet. It never saves: the caller does,
    /// so an on-chain scan and an exchange scan that share a context end up in a single transaction. New positions are
    /// added to <paramref name="db"/> and to <paramref name="positions"/>, so a later part of the same scan sees them.
    /// </summary>
    private static async Task<PositionChanges> ApplyExchangeHoldingsAsync(
        AppDbContext db,
        Wallet wallet,
        IReadOnlyCollection<ExchangePosition> holdings,
        string? error,
        DateTime nowUtc,
        List<Position> positions,
        List<WhitelistedToken> tokens,
        CancellationToken ct)
    {
        // A failed read is not a zero balance: without holdings nothing is applied and the stored positions stay.
        if (error is not null)
        {
            wallet.LastScanError = error;
            return new PositionChanges(0, 0, 0);
        }

        // Positions of an exchange account are identified by their wallet; they have no address and therefore fall
        // outside the unique (asset, chain, address) index. All of them hang off this WalletId.
        var bySymbol = holdings
            .GroupBy(h => h.Symbol.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Quantity), StringComparer.OrdinalIgnoreCase);

        int added = 0, updated = 0, removed = 0;
        const string reason = "Exchange scan";

                        // Tokens the user tracks: the whitelist decides what an exchange balance is called and how it is priced.
        // A chain is kept on the position only when it is a real blockchain; a token whitelisted as "Exchange" is
        // matched by its ticker and has no chain, because the exchange does not report one.
        // A ticker whitelisted more than once (USDC on Cronos and on BNB Smart Chain) is reported once by the
        // exchange, so the whole quantity goes to the first entry of the group and the others are left untouched. That
                // keeps one quantity per asset per exchange for the portfolio total.
        var tracked = tokens
            // Fiat cash (EUR) is handled below through the fiat path, never through the whitelist, even if an old
            // database still has a stray fiat entry - otherwise it would create a second EUR position.
            .Where(t => !FiatCurrencies.IsFiat(t.Symbol))
            .GroupBy(t => t.Symbol.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Token: g.OrderBy(t => t.Id).First(), Quantity: bySymbol.GetValueOrDefault(g.Key, 0m)))
            .ToList();

        foreach (var (token, quantity) in tracked)
        {
            var chain = token.Chain == Chain.Exchange ? (Chain?)null : token.Chain;
            var position = positions.FirstOrDefault(p =>
                p.AssetId == token.AssetId && p.SourceType == PositionSourceType.Exchange);

            if (quantity > 0)
            {
                if (position is null)
                {
                                                            var created = new Position
                    {
                        AssetId = token.AssetId, Asset = token.Asset, WalletOrExchangeName = wallet.Name,
                        SourceType = PositionSourceType.Exchange, Chain = chain, WalletAddress = null,
                        Quantity = quantity, WalletId = wallet.Id,
                        LastSyncedAtUtc = nowUtc, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
                    };
                    db.Positions.Add(created);
                    positions.Add(created);
                    LogChange(db, created, PositionChangeType.Added, null, quantity, reason, nowUtc);
                    added++;
                }
                else
                {
                    if (position.Quantity != quantity)
                    {
                        LogChange(db, position, PositionChangeType.Updated, position.Quantity, quantity, reason, nowUtc);
                                                updated++;
                    }
                    position.Quantity = quantity;
                    position.Chain = chain;
                    position.WalletOrExchangeName = wallet.Name;
                    position.LastSyncedAtUtc = nowUtc;
                    position.UpdatedAtUtc = nowUtc;
                }
            }
            else if (position is not null)
            {
                LogChange(db, position, PositionChangeType.Removed, position.Quantity, null, reason, nowUtc);
                db.Positions.Remove(position);
                positions.Remove(position);
                removed++;
            }
        }

                        // Holdings the portfolio does not track yet. An exchange reports every coin the account holds, but an exchange
        // wallet only shows what the user whitelisted, plus fiat cash (EUR): that is priced through CoinGecko so the
        // cash counts, while any other un-whitelisted coin is simply ignored (it can be whitelisted later).
        var whitelistedSymbols = tokens
            // A stray fiat whitelist entry must not stop the fiat path below from handling the EUR balance.
            .Where(t => !FiatCurrencies.IsFiat(t.Symbol))
            .Select(t => t.Symbol.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untracked = holdings
            .Where(h => !whitelistedSymbols.Contains(h.Symbol.Trim()) && FiatCurrencies.IsFiat(h.Symbol))
            .GroupBy(h => h.Symbol.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Holding: g.First(), Quantity: g.Sum(h => h.Quantity)))
            // A holding with no balance needs no position and, just as importantly, no asset: resolving the asset first
            // would otherwise create an asset that nothing points at.
            .Where(x => x.Quantity > 0)
                        .ToList();

        var untrackedAssets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (holding, _) in untracked)
            untrackedAssets[holding.Symbol] = await FindOrCreateFiatAssetAsync(db, holding, ct);

        foreach (var (holding, quantity) in untracked)
        {
            var asset = untrackedAssets[holding.Symbol];
            // New assets are not saved until the end of the scan, so several of them share Id 0 within this loop. Match
            // an existing position by the asset instance for those (a position created earlier in this same loop holds
            // the same instance), and by Id for assets that were already stored.
            var position = positions.FirstOrDefault(p =>
                p.SourceType == PositionSourceType.Exchange && p.Chain == null &&
                (ReferenceEquals(p.Asset, asset) || (asset.Id != 0 && p.AssetId == asset.Id)));

            if (position is null)
            {
                var created = new Position
                {
                    AssetId = asset.Id, Asset = asset, WalletOrExchangeName = wallet.Name,
                    SourceType = PositionSourceType.Exchange, Chain = null, WalletAddress = null,
                    Quantity = quantity, WalletId = wallet.Id,
                    LastSyncedAtUtc = nowUtc, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
                };
                db.Positions.Add(created);
                positions.Add(created);
                LogChange(db, created, PositionChangeType.Added, null, quantity, reason, nowUtc);
                added++;
            }
            else
            {
                if (position.Quantity != quantity)
                {
                    LogChange(db, position, PositionChangeType.Updated, position.Quantity, quantity, reason, nowUtc);
                    updated++;
                }
                position.Quantity = quantity;
                position.WalletOrExchangeName = wallet.Name;
                position.LastSyncedAtUtc = nowUtc;
                position.UpdatedAtUtc = nowUtc;
            }
        }

        // An un-whitelisted coin no longer shows up on an exchange wallet. A position that an earlier scan created for
        // one (a price-less "exchange-<ticker>" placeholder) is therefore dropped - unless it is fiat cash, which stays.
        foreach (var stale in positions.Where(p =>
                     p.SourceType == PositionSourceType.Exchange &&
                     ExchangePosition.IsPlaceholderId(p.Asset?.CoinGeckoId) &&
                     !FiatCurrencies.IsFiat(p.Asset?.Symbol)).ToList())
        {
            LogChange(db, stale, PositionChangeType.Removed, stale.Quantity, null, reason, nowUtc);
            db.Positions.Remove(stale);
            positions.Remove(stale);
            removed++;
        }

        return new PositionChanges(added, updated, removed);
    }

                        /// <summary>
    /// The priced asset for a fiat holding of an exchange (EUR cash). The holding is shown with the exchange's own
    /// ticker (EUR), but it must get a price, so it is mapped to a real CoinGecko coin ("euro-coin").
    /// A price-less "exchange-eur" placeholder from an earlier scan is adopted in place, so the existing position and
    /// its history are re-priced instead of being duplicated. Only fiat reaches this method; other un-whitelisted
    /// holdings are never turned into an asset.
    /// </summary>
        private static async Task<Asset> FindOrCreateFiatAssetAsync(AppDbContext db, ExchangePosition holding, CancellationToken ct)
    {
        var symbol = holding.Symbol.Trim();
        var fiatId = FiatCurrencies.CoinGeckoIdFor(symbol)!;

        // The holding is shown with the exchange's own ticker (EUR), not the coin it is priced through: the real
        // CoinGecko id ("euro-coin") only exists so the holding gets a price. An asset created by an earlier scan may
        // still carry the coin's own name ("EURC"), so it is renamed in place to keep a single, consistently named EUR.
        var fiat = await db.Assets.FirstOrDefaultAsync(a => a.CoinGeckoId == fiatId, ct)
            ?? db.Assets.Local.FirstOrDefault(a => a.CoinGeckoId == fiatId);
        if (fiat is not null)
        {
            if (fiat.Name != symbol || fiat.Symbol != symbol)
            {
                fiat.Name = symbol;
                fiat.Symbol = symbol;
            }
            return fiat;
        }

        // Earlier scans stored this fiat as a price-less placeholder ("exchange-eur"). Adopt that row and give it the
        // real CoinGecko id, so the existing position and its history are re-priced instead of being duplicated.
        var placeholderId = ExchangePosition.PlaceholderId(symbol);
        var placeholder = await db.Assets.FirstOrDefaultAsync(a => a.CoinGeckoId == placeholderId, ct)
            ?? db.Assets.Local.FirstOrDefault(a => a.CoinGeckoId == placeholderId);

        if (placeholder is null)
        {
            fiat = new Asset { Name = symbol, Symbol = symbol, CoinGeckoId = fiatId };
            db.Assets.Add(fiat);
        }
        else
        {
            fiat = placeholder;
            fiat.CoinGeckoId = fiatId;
            fiat.Name = symbol;
            fiat.Symbol = symbol;
        }

        return fiat;
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

            /// <summary>
    /// The asset for a new whitelist entry, by its CoinGecko id. When the id is new but the scan already made a
    /// price-less asset for this exchange ticker (id <c>exchange-sei</c>), that row is given the real id instead, so the
    /// holding keeps its position and history and no orphan is left behind.
    /// </summary>
    private static async Task<Asset> FindOrCreateAssetAsync(AppDbContext db, WhitelistedToken token, string coinGeckoId, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.CoinGeckoId == coinGeckoId, ct);
        if (asset is not null) return asset;

        asset = await FindPlaceholderAssetAsync(db, token, ct);
        if (asset is not null)
        {
            asset.CoinGeckoId = coinGeckoId;
            asset.Name = token.Name;
            asset.Symbol = token.Symbol;
            return asset;
        }

        asset = new Asset { Name = token.Name, Symbol = token.Symbol, CoinGeckoId = coinGeckoId };
        db.Assets.Add(asset);
        return asset;
    }

    /// <summary>
    /// The price-less asset an exchange scan created for a ticker, if any. Only exchange tokens adopt one: an on-chain
    /// token with the same ticker is a different coin, so it gets its own asset.
    /// </summary>
    private static async Task<Asset?> FindPlaceholderAssetAsync(AppDbContext db, WhitelistedToken token, CancellationToken ct)
    {
        if (token.Chain != Chain.Exchange) return null;

        var placeholderId = ExchangePosition.PlaceholderId(token.Symbol);
        return await db.Assets.SingleOrDefaultAsync(a => a.CoinGeckoId == placeholderId, ct);
    }

    /// <summary>
    /// Whether the scan can read this wallet: an on-chain address for every on-chain type, and a supported exchange as
    /// the source for a CEX (whose balances come from the exchange's API instead of an address).
    /// </summary>
    private static bool IsScannable(Wallet wallet) => wallet.Type == WalletType.Cex
        ? ExchangeCatalog.Match(wallet.Source) is not null
        : BlockchainAddress.DetectFamily(wallet.Address) is not null;
}
