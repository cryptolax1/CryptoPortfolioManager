using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public class PortfolioService(IPortfolioRepository repository, TimeProvider clock) : IPortfolioService
{
    /// <summary>
    /// A reference price may be at most this much older than the period start. Prevents a very stale snapshot
    /// from being presented as e.g. the price of one week ago.
    /// </summary>
    public static readonly TimeSpan ReferenceTolerance = TimeSpan.FromDays(1);

    public async Task<PortfolioOverview> GetPortfolioOverviewAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var targets = Enum.GetValues<DeltaPeriod>().ToDictionary(p => p, p => PeriodStart(p, now));

        var assets = await repository.GetAssetsWithPositionsAsync(ct);
        var latest = await repository.GetLatestPricesAsync(ct);
        var history = await repository.GetPriceSnapshotsSinceAsync(targets.Values.Min() - ReferenceTolerance, ct);
        var historyByAsset = history.ToLookup(s => s.AssetId);

        // The current view only holds what the wallets contain; assets without positions (e.g. whitelisted but
        // empty, or sold since) keep their price history but are not shown.
        var rows = assets.Where(a => a.Positions.Count > 0).Select(asset =>
        {
            var price = latest.GetValueOrDefault(asset.Id)?.PriceUsd;
            var snapshots = historyByAsset[asset.Id].OrderBy(s => s.TimestampUtc).ToList();

            var deltas = targets.ToDictionary(
                t => t.Key,
                t => CalculateDelta(price, FindReferencePrice(snapshots, t.Value)));

            var positions = asset.Positions
                .Select(p => new PositionLine(p.Id, p.WalletOrExchangeName, p.Chain, p.WalletAddress, p.Quantity, p.Quantity * price)
                {
                    SourceType = p.SourceType
                })
                .OrderBy(p => p.WalletOrExchangeName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var quantity = positions.Sum(p => p.Quantity);

            return new AssetOverview(asset.Id, asset.Name, asset.Symbol, asset.CoinGeckoId, quantity, price, quantity * price, deltas, positions);
        }).ToList();

        return new PortfolioOverview(rows);
    }

    public static DateTime PeriodStart(DeltaPeriod period, DateTime nowUtc) => period switch
    {
        DeltaPeriod.Ytd => new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        DeltaPeriod.ThreeMonths => nowUtc.AddMonths(-3),
        DeltaPeriod.OneMonth => nowUtc.AddMonths(-1),
        DeltaPeriod.OneWeek => nowUtc.AddDays(-7),
        DeltaPeriod.OneDay => nowUtc.AddDays(-1),
        _ => throw new ArgumentOutOfRangeException(nameof(period))
    };

    /// <summary>Oldest timestamp for which history is needed to calculate every delta (including tolerance).</summary>
    public static DateTime RequiredHistoryStart(DateTime nowUtc) =>
        Enum.GetValues<DeltaPeriod>().Min(p => PeriodStart(p, nowUtc)) - ReferenceTolerance;

    /// <summary>Latest snapshot at or before the target, if it is not older than the tolerance.</summary>
    private static decimal? FindReferencePrice(List<PriceSnapshot> ordered, DateTime target)
    {
        var reference = ordered.LastOrDefault(s => s.TimestampUtc <= target);
        return reference is not null && target - reference.TimestampUtc <= ReferenceTolerance
            ? reference.PriceUsd
            : null;
    }

    private static decimal? CalculateDelta(decimal? current, decimal? reference) =>
        current is null || reference is null or 0m ? null : current / reference - 1m;
}
