using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoPortfolioManager.Tests;

/// <summary>Controllable in-memory price provider; no network access.</summary>
public sealed class FakePriceProvider : IPriceProvider
{
    public string SourceName => "CoinGecko";

    public Dictionary<string, PriceQuote> Current { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PricePoint>> History { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? CurrentError { get; set; }
    public Dictionary<string, Exception> HistoryErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<Task>? BeforeCurrent { get; set; }

    public List<string[]> CurrentCalls { get; } = [];
    public List<(string Id, DateTime From, DateTime To)> HistoryCalls { get; } = [];

    public async Task<IReadOnlyDictionary<string, PriceQuote>> GetCurrentUsdPricesAsync(IReadOnlyCollection<string> providerIds, CancellationToken ct = default)
    {
        CurrentCalls.Add(providerIds.ToArray());
        if (BeforeCurrent is not null) await BeforeCurrent();
        if (CurrentError is not null) throw CurrentError;
        return providerIds.Where(Current.ContainsKey).ToDictionary(id => id, id => Current[id], StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<PricePoint>> GetHistoricalUsdPricesAsync(string providerId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        HistoryCalls.Add((providerId, fromUtc, toUtc));
        if (HistoryErrors.TryGetValue(providerId, out var error)) throw error;
        IReadOnlyList<PricePoint> points = History.GetValueOrDefault(providerId)?
            .Where(p => p.TimestampUtc >= fromUtc && p.TimestampUtc <= toUtc).ToList() ?? [];
        return Task.FromResult(points);
    }
}

public class PriceSyncServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private TestDatabase _db = null!;
    private FakeTimeProvider _clock = null!;
    private FakePriceProvider _provider = null!;
    private PriceSyncService _sync = null!;
    private PriceSyncRepository _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _clock = new FakeTimeProvider(new DateTimeOffset(Now));
        _provider = new FakePriceProvider();
        _repository = new PriceSyncRepository(_db.CreateFactory());
        _sync = CreateService(_provider);

        await using var db = _db.CreateContext();
        db.Assets.AddRange(
            new Asset { Name = "Bitcoin", Symbol = "BTC", CoinGeckoId = "bitcoin" },
            new Asset { Name = "Ethereum", Symbol = "ETH", CoinGeckoId = "ethereum" });
        await db.SaveChangesAsync();

        _provider.Current["bitcoin"] = new PriceQuote(62000m, Now);
        _provider.Current["ethereum"] = new PriceQuote(2500m, Now);
        _provider.History["bitcoin"] = DailyHistory(50000m);
        _provider.History["ethereum"] = DailyHistory(2000m);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private PriceSyncService CreateService(IPriceProvider provider, PriceSyncLock? gate = null) =>
        new(provider, _repository, _clock, gate ?? new PriceSyncLock(), NullLogger<PriceSyncService>.Instance);

    /// <summary>Daily 00:00 UTC points from 20 Dec 2025 up to and including 14 Oct 2026.</summary>
    private static List<PricePoint> DailyHistory(decimal start)
    {
        var first = new DateTime(2025, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        var days = (int)(Now.Date - first).TotalDays;
        return Enumerable.Range(0, days + 1).Select(i => new PricePoint(first.AddDays(i), start + i)).ToList();
    }

    private async Task<List<PriceSnapshot>> SnapshotsAsync(string coinId)
    {
        await using var db = _db.CreateContext();
        return await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.Asset!.CoinGeckoId == coinId)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();
    }

    [Fact]
    public async Task FirstSync_UsesOneCallForCurrentPricesAndOneHistoryCallPerAsset()
    {
        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.Succeeded, result.Status);
        Assert.Single(_provider.CurrentCalls);
        Assert.Equal(["bitcoin", "ethereum"], _provider.CurrentCalls[0].Order());
        Assert.Equal(2, _provider.HistoryCalls.Count);
        Assert.All(_provider.HistoryCalls, c => Assert.Equal(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), c.From));
        Assert.Equal(2, result.CurrentPricesStored);
        Assert.True(result.HistoricalSnapshotsStored > 500);
    }

    [Fact]
    public async Task FirstSync_StoresEnoughHistoryForAllDeltas()
    {
        await _sync.SyncAsync();

        var portfolio = new PortfolioService(new PortfolioRepository(_db.CreateFactory()), _clock);
        await using (var db = _db.CreateContext())
        {
            var btc = await db.Assets.SingleAsync(a => a.CoinGeckoId == "bitcoin");
            db.Positions.Add(new Position { AssetId = btc.Id, WalletOrExchangeName = "Test", Quantity = 1m, SourceType = PositionSourceType.Manual });
            await db.SaveChangesAsync();
        }

        var overview = await portfolio.GetPortfolioOverviewAsync();
        var bitcoin = overview.Assets.Single(a => a.Symbol == "BTC");

        Assert.Equal(62000m, bitcoin.PriceUsd);
        foreach (var period in Enum.GetValues<DeltaPeriod>())
            Assert.NotNull(bitcoin.DeltaFor(period));
    }

    [Fact]
    public async Task SecondSync_DoesNotRefetchHistoryAndAddsOnlyNewPrices()
    {
        await _sync.SyncAsync();
        var before = (await SnapshotsAsync("bitcoin")).Count;
        _provider.HistoryCalls.Clear();

        _clock.Advance(TimeSpan.FromHours(1));
        _provider.Current["bitcoin"] = new PriceQuote(62500m, Now.AddHours(1));
        var result = await _sync.SyncAsync();

        Assert.Empty(_provider.HistoryCalls);
        Assert.Equal(1, result.CurrentPricesStored); // bitcoin new, ethereum unchanged timestamp
        Assert.Equal(before + 1, (await SnapshotsAsync("bitcoin")).Count);
    }

    [Fact]
    public async Task RepeatedSync_WithSameTimestamp_DoesNotCreateDuplicates()
    {
        await _sync.SyncAsync();
        var before = (await SnapshotsAsync("ethereum")).Count;

        var result = await _sync.SyncAsync();

        Assert.Equal(0, result.CurrentPricesStored);
        Assert.Equal(0, result.HistoricalSnapshotsStored);
        Assert.Equal(before, (await SnapshotsAsync("ethereum")).Count);
    }

    [Fact]
    public async Task AfterGap_OnlyTheGapIsBackfilledWithoutDuplicates()
    {
        await _sync.SyncAsync();
        var before = (await SnapshotsAsync("bitcoin")).Count;
        _provider.HistoryCalls.Clear();

        _clock.Advance(TimeSpan.FromDays(5));
        var later = Now.AddDays(5);
        _provider.Current["bitcoin"] = new PriceQuote(64000m, later);
        _provider.Current["ethereum"] = new PriceQuote(2600m, later);
        _provider.History["bitcoin"].AddRange(Enumerable.Range(1, 5).Select(i => new PricePoint(Now.Date.AddDays(i), 60000m + i)));

        await _sync.SyncAsync();

        var call = Assert.Single(_provider.HistoryCalls, c => c.Id == "bitcoin");
        Assert.True(call.From >= Now.AddDays(-2));
        var snapshots = await SnapshotsAsync("bitcoin");
        Assert.Equal(snapshots.Count, snapshots.Select(s => s.TimestampUtc).Distinct().Count());
        Assert.Equal(before + 5 + 1, snapshots.Count);
    }

    [Fact]
    public async Task InvalidPrices_AreNeverStored_AndLastGoodPriceIsKept()
    {
        await _sync.SyncAsync();

        _clock.Advance(TimeSpan.FromHours(1));
        _provider.Current["bitcoin"] = new PriceQuote(0m, Now.AddHours(1));
        _provider.Current["ethereum"] = new PriceQuote(-5m, Now.AddHours(1));
        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.Failed, result.Status);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(await SnapshotsAsync("bitcoin"), s => Assert.True(s.PriceUsd > 0m));

        var latest = await new PortfolioRepository(_db.CreateFactory()).GetLatestPricesAsync();
        Assert.Contains(latest.Values, s => s.PriceUsd == 62000m);
        Assert.Contains(latest.Values, s => s.PriceUsd == 2500m);
    }

    [Fact]
    public async Task MissingPrice_ForOneAsset_GivesPartialSuccess()
    {
        _provider.Current.Remove("ethereum");

        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.PartiallySucceeded, result.Status);
        Assert.Contains(result.Issues, i => i.Contains("ETH"));
        Assert.Single(await SnapshotsAsync("bitcoin"), s => s.PriceUsd == 62000m);
    }

    [Fact]
    public async Task RateLimitOnCurrentPrices_FailsRun_WithoutHistoryCalls_AndKeepsOldData()
    {
        await _sync.SyncAsync();
        var before = (await SnapshotsAsync("bitcoin")).Count;
        _provider.HistoryCalls.Clear();

        _clock.Advance(TimeSpan.FromHours(1));
        _provider.CurrentError = new PriceProviderException(PriceProviderErrorKind.RateLimited, "Rate limit exceeded (429).");
        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.Failed, result.Status);
        Assert.Empty(_provider.HistoryCalls);
        Assert.Equal(before, (await SnapshotsAsync("bitcoin")).Count);

        var status = await _sync.GetStatusAsync();
        Assert.Equal(PriceUpdateStatus.Failed, status.LatestLog!.Status);
        Assert.Contains("429", status.LatestLog.ErrorMessage);
        Assert.Equal(Now, status.LastSuccessfulUtc); // the earlier successful update is still reported
    }

    [Fact]
    public async Task HistoryFailureForOneAsset_IsPartial_AndOtherAssetsContinue()
    {
        _provider.HistoryErrors["bitcoin"] = new PriceProviderException(PriceProviderErrorKind.Timeout, "Request timed out.");

        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.PartiallySucceeded, result.Status);
        Assert.Equal(2, result.CurrentPricesStored);
        Assert.Equal(2, _provider.HistoryCalls.Count);
        Assert.True((await SnapshotsAsync("ethereum")).Count > 100);
    }

    [Fact]
    public async Task HistoryRateLimit_StopsFurtherHistoryCalls()
    {
        _provider.HistoryErrors["bitcoin"] = new PriceProviderException(PriceProviderErrorKind.RateLimited, "429");

        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.PartiallySucceeded, result.Status);
        Assert.Single(_provider.HistoryCalls);
    }

    [Fact]
    public async Task EmptyHistoryResponse_IsReported_NotTreatedAsZeroPrices()
    {
        _provider.History["ethereum"].Clear();

        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.PartiallySucceeded, result.Status);
        Assert.Contains(result.Issues, i => i.Contains("No history") && i.Contains("ETH"));
        Assert.Single(await SnapshotsAsync("ethereum"));
    }

    [Fact]
    public async Task SuccessfulSync_WritesLogWithTimes()
    {
        var result = await _sync.SyncAsync();

        var status = await _sync.GetStatusAsync();
        Assert.Equal(PriceUpdateStatus.Succeeded, result.Status);
        Assert.Equal(Now, status.LastSuccessfulUtc);
        Assert.Equal(PriceUpdateStatus.Succeeded, status.LatestLog!.Status);
        Assert.Null(status.LatestLog.ErrorMessage);
        Assert.Equal(Now, status.LatestLog.StartedAtUtc);
    }

    [Fact]
    public async Task RecentLogs_AreReturnedNewestFirst()
    {
        await _sync.SyncAsync();
        await _sync.SyncAsync();

        var logs = await _sync.GetRecentLogsAsync(10);

        Assert.Equal(2, logs.Count);
        Assert.True(logs[0].Id > logs[1].Id);
    }

    [Fact]
    public async Task SecondSyncWhileRunning_ReturnsBusy_AndRunsNothing()
    {
        var gate = new PriceSyncLock();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _provider.BeforeCurrent = async () => { entered.SetResult(); await release.Task; };
        var first = CreateService(_provider, gate);
        var second = CreateService(new FakePriceProvider(), gate);

        var running = first.SyncAsync();
        await entered.Task;
        var busy = await second.SyncAsync();
        release.SetResult();
        await running;

        Assert.True(busy.AlreadyRunning);
    }

    [Fact]
    public async Task StaleRunningLog_IsMarkedFailedOnNextSync()
    {
        await _repository.StartUpdateLogAsync(Now.AddHours(-3));

        await _sync.SyncAsync();

        await using var db = _db.CreateContext();
        var logs = await db.PriceUpdateLogs.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(PriceUpdateStatus.Failed, logs[0].Status);
        Assert.Contains("interrupted", logs[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PriceUpdateStatus.Succeeded, logs[1].Status);
    }

    [Fact]
    public async Task NoAssets_SucceedsWithoutProviderCalls()
    {
        await using (var db = _db.CreateContext())
        {
            db.Assets.RemoveRange(db.Assets);
            await db.SaveChangesAsync();
        }

        var result = await _sync.SyncAsync();

        Assert.Equal(PriceUpdateStatus.Succeeded, result.Status);
        Assert.Empty(_provider.CurrentCalls);
    }

    [Fact]
    public void DetermineHistoryStart_CoversNewRecentAndGapCases()
    {
        var required = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(required, PriceSyncService.DetermineHistoryStart(null, required, Now));
        Assert.Equal(required, PriceSyncService.DetermineHistoryStart(new SnapshotCoverage(Now.AddDays(-1), Now), required, Now));
        Assert.Null(PriceSyncService.DetermineHistoryStart(new SnapshotCoverage(required, Now.AddHours(-3)), required, Now));
        Assert.Equal(Now.AddDays(-9), PriceSyncService.DetermineHistoryStart(new SnapshotCoverage(required, Now.AddDays(-8)), required, Now));
    }
}
