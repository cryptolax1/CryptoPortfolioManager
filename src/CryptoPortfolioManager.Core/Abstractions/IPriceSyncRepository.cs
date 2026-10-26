using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Abstractions;

public sealed record SnapshotCoverage(DateTime EarliestUtc, DateTime LatestUtc);

/// <summary>Persistence port for price synchronization; implemented in Infrastructure.</summary>
public interface IPriceSyncRepository
{
    Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct = default);

    /// <summary>Earliest and latest snapshot per asset id for one source.</summary>
    Task<IReadOnlyDictionary<int, SnapshotCoverage>> GetSnapshotCoverageAsync(string source, CancellationToken ct = default);

    /// <summary>
    /// Inserts snapshots that do not exist yet (same asset, source and timestamp). Existing rows are never changed.
    /// Returns the number of inserted rows.
    /// </summary>
    Task<int> AddSnapshotsAsync(int assetId, string source, IReadOnlyCollection<PricePoint> points, CancellationToken ct = default);

    /// <summary>Marks logs that are still "Running" (left behind by a crash) as failed.</summary>
    Task MarkInterruptedUpdatesAsync(DateTime nowUtc, CancellationToken ct = default);

    Task<int> StartUpdateLogAsync(DateTime startedAtUtc, CancellationToken ct = default);

    Task CompleteUpdateLogAsync(int logId, DateTime completedAtUtc, PriceUpdateStatus status, string? errorMessage, CancellationToken ct = default);

    Task<PriceUpdateLog?> GetLatestUpdateLogAsync(CancellationToken ct = default);

    /// <summary>Newest-first price update logs.</summary>
    Task<IReadOnlyList<PriceUpdateLog>> GetRecentUpdateLogsAsync(int take, CancellationToken ct = default);

    Task<PriceUpdateLog?> GetLastSuccessfulUpdateLogAsync(CancellationToken ct = default);
}
