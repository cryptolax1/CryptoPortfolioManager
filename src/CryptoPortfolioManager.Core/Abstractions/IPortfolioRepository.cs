using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Abstractions;

/// <summary>Persistence port; implemented in the Infrastructure project.</summary>
public interface IPortfolioRepository
{
    /// <summary>All assets including their positions.</summary>
    Task<IReadOnlyList<Asset>> GetAssetsWithPositionsAsync(CancellationToken ct = default);

    /// <summary>Most recent snapshot per asset id.</summary>
    Task<IReadOnlyDictionary<int, PriceSnapshot>> GetLatestPricesAsync(CancellationToken ct = default);

    /// <summary>All snapshots with a timestamp at or after <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<PriceSnapshot>> GetPriceSnapshotsSinceAsync(DateTime sinceUtc, CancellationToken ct = default);
}
