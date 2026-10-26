using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace CryptoPortfolioManager.Tests;

public class PortfolioServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TestFactory(TestDatabase db) : Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => db.CreateContext();
    }

    private static PortfolioService CreateService(TestDatabase db) =>
        new(new PortfolioRepository(new TestFactory(db)), new FakeTimeProvider(new DateTimeOffset(Now)));

    private static async Task<int> AddAssetAsync(TestDatabase testDb, string symbol, decimal[] quantities, params (DateTime At, decimal Price)[] prices)
    {
        await using var db = testDb.CreateContext();
        var asset = new Asset { Name = symbol + " coin", Symbol = symbol, CoinGeckoId = symbol.ToLowerInvariant() };
        foreach (var q in quantities)
            db.Positions.Add(new Position { Asset = asset, WalletOrExchangeName = "W" + q, Quantity = q });
        foreach (var (at, price) in prices)
            db.PriceSnapshots.Add(new PriceSnapshot { Asset = asset, PriceUsd = price, TimestampUtc = at, Source = "Test" });
        if (quantities.Length == 0) db.Assets.Add(asset);
        await db.SaveChangesAsync(Ct);
        return asset.Id;
    }

    [Fact]
    public async Task EmptyDatabase_GivesEmptyOverview()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        var overview = await CreateService(testDb).GetPortfolioOverviewAsync(Ct);

        Assert.Empty(overview.Assets);
        Assert.Equal(0m, overview.TotalValueUsd);
    }

    [Fact]
    public async Task QuantityAndValue_AreSummedFromPositions()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await AddAssetAsync(testDb, "AAA", [1.5m, 2.5m], (Now, 100m));

        var asset = Assert.Single((await CreateService(testDb).GetPortfolioOverviewAsync(Ct)).Assets);

        Assert.Equal(4m, asset.Quantity);
        Assert.Equal(100m, asset.PriceUsd);
        Assert.Equal(400m, asset.ValueUsd);
        Assert.Equal(asset.ValueUsd, asset.Positions.Sum(p => p.ValueUsd));
        Assert.Equal(2, asset.Positions.Count);
    }

    [Fact]
    public async Task WithoutPrice_ValueAndDeltasAreNull_AndExcludedFromTotal()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await AddAssetAsync(testDb, "NOP", [3m]);
        await AddAssetAsync(testDb, "PRC", [1m], (Now, 10m));

        var overview = await CreateService(testDb).GetPortfolioOverviewAsync(Ct);
        var noPrice = overview.Assets.Single(a => a.Symbol == "NOP");

        Assert.Null(noPrice.PriceUsd);
        Assert.Null(noPrice.ValueUsd);
        Assert.All(Enum.GetValues<DeltaPeriod>(), p => Assert.Null(noPrice.DeltaFor(p)));
        Assert.Equal(10m, overview.TotalValueUsd);
        Assert.Equal(1, overview.AssetsWithoutPrice);
    }

    [Fact]
    public async Task Deltas_AreOnlyCalculatedWhereHistoryExists()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await AddAssetAsync(testDb, "AAA", [1m],
            (Now, 120m),
            (Now.AddDays(-1), 100m),   // exactly 1D ago
            (Now.AddDays(-7), 80m));   // exactly 1W ago

        var asset = Assert.Single((await CreateService(testDb).GetPortfolioOverviewAsync(Ct)).Assets);

        Assert.Equal(0.2m, asset.DeltaFor(DeltaPeriod.OneDay));
        Assert.Equal(0.5m, asset.DeltaFor(DeltaPeriod.OneWeek));
        Assert.Null(asset.DeltaFor(DeltaPeriod.OneMonth));
        Assert.Null(asset.DeltaFor(DeltaPeriod.ThreeMonths));
        Assert.Null(asset.DeltaFor(DeltaPeriod.Ytd));
    }

    [Fact]
    public async Task Deltas_NegativeChangeAndYtdUseStartOfYear()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await AddAssetAsync(testDb, "AAA", [1m],
            (Now, 50m),
            (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 200m));

        var asset = Assert.Single((await CreateService(testDb).GetPortfolioOverviewAsync(Ct)).Assets);

        Assert.Equal(-0.75m, asset.DeltaFor(DeltaPeriod.Ytd));
    }

    [Fact]
    public async Task StaleSnapshot_IsNotUsedAsReferencePrice()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        // Only reference is 5 days before the 1W target: outside tolerance.
        await AddAssetAsync(testDb, "AAA", [1m], (Now, 120m), (Now.AddDays(-12), 100m));

        var asset = Assert.Single((await CreateService(testDb).GetPortfolioOverviewAsync(Ct)).Assets);

        Assert.Null(asset.DeltaFor(DeltaPeriod.OneWeek));
    }

    [Fact]
    public async Task AssetWithoutPositions_IsNotShown()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await AddAssetAsync(testDb, "AAA", [], (Now, 10m));

        Assert.Empty((await CreateService(testDb).GetPortfolioOverviewAsync(Ct)).Assets);
    }

    [Fact]
    public async Task SeededData_ProducesExpectedTotals()
    {
        await using var testDb = await TestDatabase.CreateAsync(seed: true);
        var overview = await CreateService(testDb).GetPortfolioOverviewAsync(Ct);

        var btc = overview.Assets.Single(a => a.Symbol == "BTC");
        Assert.Equal(0.35m, btc.Quantity);
        Assert.Equal(0.35m * btc.PriceUsd, btc.ValueUsd);
        Assert.Equal(3, overview.Assets.Count);
    }
}

public class AssetSorterTests
{
    private static AssetOverview Make(string name, decimal quantity, decimal? price, decimal? delta1d = null) => new(
        name.GetHashCode(), name, name[..3].ToUpperInvariant(), name.ToLowerInvariant(), quantity, price, price * quantity,
        new Dictionary<DeltaPeriod, decimal?> { [DeltaPeriod.OneDay] = delta1d }, []);

    [Fact]
    public void Value_SortsDescending_WithUnpricedLast()
    {
        var rows = new[] { Make("Alpha", 1, 10), Make("Beta", 1, null), Make("Gamma", 1, 50) };

        var sorted = AssetSorter.Sort(rows, AssetSortColumn.Value, descending: true);

        Assert.Equal(["Gamma", "Alpha", "Beta"], sorted.Select(a => a.Name));
    }

    [Fact]
    public void Value_Ascending_StillPutsUnpricedLast()
    {
        var rows = new[] { Make("Alpha", 1, 10), Make("Beta", 1, null), Make("Gamma", 1, 50) };

        var sorted = AssetSorter.Sort(rows, AssetSortColumn.Value, descending: false);

        Assert.Equal(["Alpha", "Gamma", "Beta"], sorted.Select(a => a.Name));
    }

    [Fact]
    public void Name_SortsCaseInsensitive()
    {
        var rows = new[] { Make("beta", 1, 1), Make("Alpha", 1, 1), Make("Gamma", 1, 1) };

        Assert.Equal(["Alpha", "beta", "Gamma"], AssetSorter.Sort(rows, AssetSortColumn.Name, false).Select(a => a.Name));
        Assert.Equal(["Gamma", "beta", "Alpha"], AssetSorter.Sort(rows, AssetSortColumn.Name, true).Select(a => a.Name));
    }

    [Fact]
    public void Delta_SortsNegativeBelowPositive_NullLast()
    {
        var rows = new[] { Make("Alpha", 1, 1, -0.1m), Make("Beta", 1, 1), Make("Gamma", 1, 1, 0.2m) };

        var sorted = AssetSorter.Sort(rows, AssetSortColumn.Delta1D, descending: true);

        Assert.Equal(["Gamma", "Alpha", "Beta"], sorted.Select(a => a.Name));
    }
}
