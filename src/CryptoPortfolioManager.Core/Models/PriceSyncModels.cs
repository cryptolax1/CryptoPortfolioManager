using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Models;

public sealed record PriceSyncResult(
    PriceUpdateStatus Status,
    int AssetCount,
    int CurrentPricesStored,
    int HistoricalSnapshotsStored,
    IReadOnlyList<string> Issues,
    bool AlreadyRunning = false)
{
    public static PriceSyncResult Busy { get; } = new(PriceUpdateStatus.Running, 0, 0, 0, [], AlreadyRunning: true);
}

/// <summary>What the UI shows about price updates.</summary>
public sealed record PriceSyncStatus(DateTime? LastSuccessfulUtc, PriceUpdateLog? LatestLog);
