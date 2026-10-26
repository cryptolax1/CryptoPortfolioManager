using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

public class PriceSyncRepository(IDbContextFactory<AppDbContext> factory) : IPriceSyncRepository
{
    public async Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Assets.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, SnapshotCoverage>> GetSnapshotCoverageAsync(string source, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.Source == source)
            .GroupBy(s => s.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(s => s.TimestampUtc), Latest = g.Max(s => s.TimestampUtc) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.AssetId, r => new SnapshotCoverage(r.Earliest, r.Latest));
    }

    public async Task<int> AddSnapshotsAsync(int assetId, string source, IReadOnlyCollection<PricePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return 0;

        var byTimestamp = points.GroupBy(p => p.TimestampUtc).Select(g => g.Last()).ToList();
        var min = byTimestamp.Min(p => p.TimestampUtc);
        var max = byTimestamp.Max(p => p.TimestampUtc);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = (await db.PriceSnapshots.AsNoTracking()
                .Where(s => s.AssetId == assetId && s.Source == source && s.TimestampUtc >= min && s.TimestampUtc <= max)
                .Select(s => s.TimestampUtc)
                .ToListAsync(ct))
            .ToHashSet();

        var added = 0;
        foreach (var point in byTimestamp.Where(p => !existing.Contains(p.TimestampUtc)))
        {
            db.PriceSnapshots.Add(new PriceSnapshot
            {
                AssetId = assetId,
                Source = source,
                PriceUsd = point.PriceUsd,
                TimestampUtc = DateTime.SpecifyKind(point.TimestampUtc, DateTimeKind.Utc)
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task MarkInterruptedUpdatesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.PriceUpdateLogs
            .Where(l => l.Status == PriceUpdateStatus.Running)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, PriceUpdateStatus.Failed)
                .SetProperty(l => l.CompletedAtUtc, nowUtc)
                .SetProperty(l => l.ErrorMessage, "Update interrupted (application stopped during the update)."), ct);
    }

    public async Task<int> StartUpdateLogAsync(DateTime startedAtUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var log = new PriceUpdateLog { StartedAtUtc = startedAtUtc, Status = PriceUpdateStatus.Running };
        db.PriceUpdateLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return log.Id;
    }

    public async Task CompleteUpdateLogAsync(int logId, DateTime completedAtUtc, PriceUpdateStatus status, string? errorMessage, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var log = await db.PriceUpdateLogs.SingleAsync(l => l.Id == logId, ct);
        log.CompletedAtUtc = completedAtUtc;
        log.Status = status;
        log.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PriceUpdateLog?> GetLatestUpdateLogAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PriceUpdateLogs.AsNoTracking().OrderByDescending(l => l.StartedAtUtc).ThenByDescending(l => l.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<PriceUpdateLog>> GetRecentUpdateLogsAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Timestamps are ISO text in SQLite; Id (insert order) is the reliable newest-first order.
        return await db.PriceUpdateLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(take).ToListAsync(ct);
    }

    public async Task<PriceUpdateLog?> GetLastSuccessfulUpdateLogAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PriceUpdateLogs.AsNoTracking()
            .Where(l => l.Status == PriceUpdateStatus.Succeeded)
            .OrderByDescending(l => l.StartedAtUtc).ThenByDescending(l => l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
