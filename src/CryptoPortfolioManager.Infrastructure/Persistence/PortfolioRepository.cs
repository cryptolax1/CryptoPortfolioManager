using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

// Uses a short-lived context per call (IDbContextFactory), which is the safe pattern for Blazor Server.
public class PortfolioRepository(IDbContextFactory<AppDbContext> factory) : IPortfolioRepository
{
    public async Task<IReadOnlyList<Asset>> GetAssetsWithPositionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Assets.AsNoTracking().Include(a => a.Positions).AsSplitQuery().ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, PriceSnapshot>> GetLatestPricesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var latest = await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.TimestampUtc == db.PriceSnapshots
                .Where(o => o.AssetId == s.AssetId)
                .Max(o => o.TimestampUtc))
            .ToListAsync(ct);

        // Several sources may share the newest timestamp; keep one per asset.
        return latest.GroupBy(s => s.AssetId).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyList<PriceSnapshot>> GetPriceSnapshotsSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.TimestampUtc >= sinceUtc)
            .ToListAsync(ct);
    }
}
