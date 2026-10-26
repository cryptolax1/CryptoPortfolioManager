using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public interface IPriceSyncService
{
    /// <summary>Fetches current prices (and missing history) for all assets and stores new snapshots.</summary>
    Task<PriceSyncResult> SyncAsync(CancellationToken ct = default);

    Task<PriceSyncStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Newest-first log of earlier price updates.</summary>
    Task<IReadOnlyList<PriceUpdateLog>> GetRecentLogsAsync(int take = 100, CancellationToken ct = default);
}
