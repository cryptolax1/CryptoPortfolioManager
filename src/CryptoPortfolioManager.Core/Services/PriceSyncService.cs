using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CryptoPortfolioManager.Core.Services;

/// <summary>Ensures only one price synchronization runs at a time (registered as singleton).</summary>
public sealed class PriceSyncLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public class PriceSyncService(
    IPriceProvider provider,
    IPriceSyncRepository repository,
    TimeProvider clock,
    PriceSyncLock syncLock,
    ILogger<PriceSyncService> logger) : IPriceSyncService
{
    /// <summary>Provider history is limited on the free plan; never request further back than this.</summary>
    public const int MaxHistoryDays = 365;

    private const int CurrentPriceBatchSize = 100;
    private const int MaxReportedIssues = 20;
    private const int MaxErrorLength = 4000;

    /// <summary>If the newest stored snapshot is older than this, the gap is back-filled.</summary>
    public static readonly TimeSpan GapThreshold = TimeSpan.FromDays(2);

    public async Task<PriceSyncStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var last = await repository.GetLastSuccessfulUpdateLogAsync(ct);
        var latest = await repository.GetLatestUpdateLogAsync(ct);
        return new PriceSyncStatus(last?.CompletedAtUtc, latest);
    }

    public Task<IReadOnlyList<PriceUpdateLog>> GetRecentLogsAsync(int take = 100, CancellationToken ct = default) =>
        repository.GetRecentUpdateLogsAsync(Math.Clamp(take, 1, 1000), ct);

    public async Task<PriceSyncResult> SyncAsync(CancellationToken ct = default)
    {
        if (!await syncLock.Semaphore.WaitAsync(0, CancellationToken.None))
            return PriceSyncResult.Busy;

        try
        {
            await repository.MarkInterruptedUpdatesAsync(Now(), CancellationToken.None);
            var logId = await repository.StartUpdateLogAsync(Now(), CancellationToken.None);

            PriceSyncResult result;
            try
            {
                result = await RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await repository.CompleteUpdateLogAsync(logId, Now(), PriceUpdateStatus.Failed, "Update cancelled.", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Price synchronization failed unexpectedly");
                await repository.CompleteUpdateLogAsync(logId, Now(), PriceUpdateStatus.Failed, Truncate($"Unexpected error: {ex.Message}"), CancellationToken.None);
                return new PriceSyncResult(PriceUpdateStatus.Failed, 0, 0, 0, [ex.Message]);
            }

            var error = result.Issues.Count == 0 ? null : Truncate(FormatIssues(result.Issues));
            await repository.CompleteUpdateLogAsync(logId, Now(), result.Status, error, CancellationToken.None);
            return result;
        }
        finally
        {
            syncLock.Semaphore.Release();
        }
    }

    private async Task<PriceSyncResult> RunAsync(CancellationToken ct)
    {
        var now = Now();
        var source = provider.SourceName;
        var issues = new List<string>();

                // Only assets with a real CoinGecko id can be priced. An exchange holding that was not whitelisted has a made-up
                // placeholder id ("exchange-sei") that CoinGecko does not know: requesting it only produces a "not found" error,
                // so it is skipped. Fiat cash (EUR) is mapped to "euro-coin", a real id, so it is priced normally.
                var assets = (await repository.GetAssetsAsync(ct))
                    .Where(a => !string.IsNullOrWhiteSpace(a.CoinGeckoId) && !ExchangePosition.IsPlaceholderId(a.CoinGeckoId))
                    .ToList();
        if (assets.Count == 0)
            return new PriceSyncResult(PriceUpdateStatus.Succeeded, 0, 0, 0, []);

        // Read before storing new prices, otherwise a freshly inserted price hides a missing history.
        var coverage = await repository.GetSnapshotCoverageAsync(source, ct);

        var (assetsWithPrice, currentStored, stopped) = await SyncCurrentPricesAsync(assets, source, now, issues, ct);

        var historyStored = 0;
        if (!stopped)
            historyStored = await SyncHistoryAsync(assets, coverage, source, now, issues, ct);

        var status = issues.Count == 0 ? PriceUpdateStatus.Succeeded
            : assetsWithPrice > 0 ? PriceUpdateStatus.PartiallySucceeded
            : PriceUpdateStatus.Failed;

        return new PriceSyncResult(status, assets.Count, currentStored, historyStored, issues);
    }

    private async Task<(int AssetsWithPrice, int Stored, bool Stopped)> SyncCurrentPricesAsync(
        List<Asset> assets, string source, DateTime now, List<string> issues, CancellationToken ct)
    {
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase);
        var ids = assets.Select(a => a.CoinGeckoId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var failedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in ids.Chunk(CurrentPriceBatchSize))
        {
            try
            {
                foreach (var (id, quote) in await provider.GetCurrentUsdPricesAsync(batch, ct))
                    quotes[id] = quote;
            }
            catch (PriceProviderException ex)
            {
                logger.LogWarning(ex, "Fetching current prices failed ({Kind})", ex.Kind);
                issues.Add($"Current prices: {ex.Message}");
                if (ex.StopsRun) return (0, 0, true);
                failedIds.UnionWith(batch);
            }
        }

        var withPrice = 0;
        var stored = 0;
        foreach (var asset in assets)
        {
            if (!quotes.TryGetValue(asset.CoinGeckoId.Trim(), out var quote))
            {
                // The failed request was already reported; don't add a message per asset.
                if (!failedIds.Contains(asset.CoinGeckoId.Trim()))
                    issues.Add($"No price received for {asset.Symbol} ({asset.CoinGeckoId}).");
                continue;
            }

            if (!IsValidPrice(quote.PriceUsd))
            {
                issues.Add($"Invalid price ignored for {asset.Symbol} ({asset.CoinGeckoId}).");
                continue;
            }

            withPrice++;
            var timestamp = quote.TimestampUtc is { } t && t <= now.AddMinutes(5) ? t : now;
            stored += await repository.AddSnapshotsAsync(asset.Id, source, [new PricePoint(timestamp, quote.PriceUsd)], ct);
        }

        return (withPrice, stored, false);
    }

    private async Task<int> SyncHistoryAsync(
        List<Asset> assets, IReadOnlyDictionary<int, SnapshotCoverage> coverage, string source, DateTime now,
        List<string> issues, CancellationToken ct)
    {
        var requiredStart = PortfolioService.RequiredHistoryStart(now);
        var oldestAllowed = now.AddDays(-MaxHistoryDays);
        if (requiredStart < oldestAllowed) requiredStart = oldestAllowed;

        var stored = 0;
        foreach (var asset in assets)
        {
            var from = DetermineHistoryStart(coverage.GetValueOrDefault(asset.Id), requiredStart, now);
            if (from is null) continue;

            try
            {
                var points = (await provider.GetHistoricalUsdPricesAsync(asset.CoinGeckoId.Trim(), from.Value, now, ct))
                    .Where(p => IsValidPrice(p.PriceUsd))
                    .ToList();

                if (points.Count == 0)
                {
                    issues.Add($"No history available for {asset.Symbol} ({asset.CoinGeckoId}).");
                    continue;
                }

                stored += await repository.AddSnapshotsAsync(asset.Id, source, points, ct);
            }
            catch (PriceProviderException ex)
            {
                logger.LogWarning(ex, "Fetching history for {CoinId} failed ({Kind})", asset.CoinGeckoId, ex.Kind);
                issues.Add($"History {asset.Symbol}: {ex.Message}");
                if (ex.StopsRun) break;
            }
        }

        return stored;
    }

    /// <summary>
    /// Null when stored history is sufficient. Otherwise the start of the range to fetch: the full required
    /// window for a new asset, or just the gap since the newest snapshot (e.g. the app was not used for days).
    /// </summary>
    public static DateTime? DetermineHistoryStart(SnapshotCoverage? coverage, DateTime requiredStart, DateTime now)
    {
        if (coverage is null || coverage.EarliestUtc > requiredStart + PortfolioService.ReferenceTolerance)
            return requiredStart;

        if (now - coverage.LatestUtc > GapThreshold)
        {
            var gapStart = coverage.LatestUtc.AddDays(-1);
            return gapStart < requiredStart ? requiredStart : gapStart;
        }

        return null;
    }

    private static bool IsValidPrice(decimal price) => price > 0m;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string FormatIssues(IReadOnlyList<string> issues)
    {
        var lines = issues.Take(MaxReportedIssues).ToList();
        if (issues.Count > MaxReportedIssues) lines.Add($"… and {issues.Count - MaxReportedIssues} more.");
        return string.Join('\n', lines);
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 1)] + "…";
}
