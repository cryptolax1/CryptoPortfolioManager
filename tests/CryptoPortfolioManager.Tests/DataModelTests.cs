using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Tests;

public class DataModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_MatchCurrentModel()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task SeededData_CanBeReadBackWithRelationships()
    {
        await using var testDb = await TestDatabase.CreateAsync(seed: true);
        await using var db = testDb.CreateContext();

        var btc = await db.Assets.Include(a => a.Positions).SingleAsync(a => a.Symbol == "BTC", Ct);
        Assert.Equal(2, btc.Positions.Count);
        Assert.All(btc.Positions, p => Assert.Equal(btc.Id, p.AssetId));

        var eth = await db.Assets.Include(a => a.Positions).SingleAsync(a => a.Symbol == "ETH", Ct);
        Assert.Equal(3.44m, eth.Positions.Single(p => p.WalletOrExchangeName == "Ledger").Quantity);
        Assert.Equal(2.41m, eth.Positions.Single(p => p.WalletOrExchangeName == "Bitvavo").Quantity);

        var usdc = await db.Assets.SingleAsync(a => a.CoinGeckoId == "usd-coin", Ct);
        Assert.Equal(Chain.Ethereum, usdc.ContractChain);
        Assert.Equal(6, usdc.TokenDecimals);

        Assert.Equal(5, await db.Positions.CountAsync(Ct));
        Assert.Equal(2, await db.PriceUpdateLogs.CountAsync(Ct));
        Assert.Contains(await db.PriceUpdateLogs.ToListAsync(Ct), l => l.Status == PriceUpdateStatus.Failed && l.ErrorMessage != null);
    }

    [Fact]
    public async Task PriceHistory_IsStoredPerAsset()
    {
        await using var testDb = await TestDatabase.CreateAsync(seed: true);
        await using var db = testDb.CreateContext();

        var eth = await db.Assets.SingleAsync(a => a.Symbol == "ETH", Ct);
        var history = await db.PriceSnapshots.Where(s => s.AssetId == eth.Id).OrderBy(s => s.TimestampUtc).ToListAsync(Ct);

        Assert.Equal([2400m, 2450.25m, 2500m], history.Select(s => s.PriceUsd));
        Assert.All(history, s => Assert.Equal(DateTimeKind.Utc, s.TimestampUtc.Kind));
    }

    [Fact]
    public async Task Decimals_KeepFullPrecision()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        const decimal tiny = 0.000000000000000001m;
        const decimal large = 12345678901234567890.123456789012345678m;

        await using (var db = testDb.CreateContext())
        {
            var asset = new Asset { Name = "Test", Symbol = "TST", CoinGeckoId = "test" };
            db.Positions.Add(new Position { Asset = asset, WalletOrExchangeName = "W", Quantity = large });
            db.PriceSnapshots.Add(new PriceSnapshot
            {
                Asset = asset, PriceUsd = tiny, Source = "Test", TimestampUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(large, (await db.Positions.SingleAsync(Ct)).Quantity);
            Assert.Equal(tiny, (await db.PriceSnapshots.SingleAsync(Ct)).PriceUsd);
        }
    }

    [Fact]
    public async Task Timestamps_AreReturnedAsUtc()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        var local = new DateTime(2026, 6, 1, 14, 0, 0, DateTimeKind.Local);

        await using (var db = testDb.CreateContext())
        {
            db.PriceUpdateLogs.Add(new PriceUpdateLog { StartedAtUtc = local, CompletedAtUtc = local.AddMinutes(1) });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = testDb.CreateContext())
        {
            var log = await db.PriceUpdateLogs.SingleAsync(Ct);
            Assert.Equal(DateTimeKind.Utc, log.StartedAtUtc.Kind);
            Assert.Equal(local.ToUniversalTime(), log.StartedAtUtc);
            Assert.Equal(local.ToUniversalTime().AddMinutes(1), log.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task Position_WithoutExistingAsset_IsRejectedByForeignKey()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();

        db.Positions.Add(new Position { AssetId = 999, WalletOrExchangeName = "W", Quantity = 1m });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DeletingAsset_WithPositions_IsRestricted()
    {
        await using var testDb = await TestDatabase.CreateAsync(seed: true);
        await using var db = testDb.CreateContext();

        var btc = await db.Assets.SingleAsync(a => a.Symbol == "BTC", Ct);
        db.Assets.Remove(btc);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DeletingAsset_WithoutPositions_RemovesItsPriceHistory()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();

        var asset = new Asset { Name = "Test", Symbol = "TST", CoinGeckoId = "test" };
        db.PriceSnapshots.Add(new PriceSnapshot { Asset = asset, PriceUsd = 1m, Source = "Test", TimestampUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);

        db.Assets.Remove(asset);
        await db.SaveChangesAsync(Ct);

        Assert.Empty(await db.PriceSnapshots.ToListAsync(Ct));
    }

    [Fact]
    public async Task CoinGeckoId_MustBeUnique()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();

        db.Assets.Add(new Asset { Name = "A", Symbol = "A", CoinGeckoId = "same" });
        db.Assets.Add(new Asset { Name = "B", Symbol = "B", CoinGeckoId = "same" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ContractAddress_MustBeUniquePerChain_ButNativeCoinsMayRepeatNull()
    {
        await using var testDb = await TestDatabase.CreateAsync();

        await using (var db = testDb.CreateContext())
        {
            db.Assets.Add(new Asset { Name = "N1", Symbol = "N1", CoinGeckoId = "n1" });
            db.Assets.Add(new Asset { Name = "N2", Symbol = "N2", CoinGeckoId = "n2" });
            db.Assets.Add(new Asset { Name = "T1", Symbol = "T1", CoinGeckoId = "t1", ContractChain = Chain.Ethereum, ContractAddress = "0xabc" });
            db.Assets.Add(new Asset { Name = "T2", Symbol = "T2", CoinGeckoId = "t2", ContractChain = Chain.Polygon, ContractAddress = "0xabc" });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = testDb.CreateContext())
        {
            db.Assets.Add(new Asset { Name = "T3", Symbol = "T3", CoinGeckoId = "t3", ContractChain = Chain.Ethereum, ContractAddress = "0xabc" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task WalletPosition_MustBeUniquePerAssetChainAndAddress_ButManualPositionsMayRepeat()
    {
        await using var testDb = await TestDatabase.CreateAsync();

        await using (var db = testDb.CreateContext())
        {
            var asset = new Asset { Name = "Test", Symbol = "TST", CoinGeckoId = "test" };
            db.Positions.Add(new Position { Asset = asset, WalletOrExchangeName = "Manual 1", Quantity = 1m });
            db.Positions.Add(new Position { Asset = asset, WalletOrExchangeName = "Manual 2", Quantity = 2m });
            db.Positions.Add(new Position
            {
                Asset = asset, WalletOrExchangeName = "W", SourceType = PositionSourceType.OnChainWallet,
                Chain = Chain.Ethereum, WalletAddress = "0x1", Quantity = 1m
            });
            await db.SaveChangesAsync(Ct);

            var assetId = asset.Id;
            await using var db2 = testDb.CreateContext();
            db2.Positions.Add(new Position
            {
                AssetId = assetId, WalletOrExchangeName = "W copy", SourceType = PositionSourceType.OnChainWallet,
                Chain = Chain.Ethereum, WalletAddress = "0x1", Quantity = 5m
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task DuplicateSnapshot_ForSameAssetTimeAndSource_IsRejected()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();
        var ts = DateTime.UtcNow;

        var asset = new Asset { Name = "Test", Symbol = "TST", CoinGeckoId = "test" };
        db.PriceSnapshots.Add(new PriceSnapshot { Asset = asset, PriceUsd = 1m, Source = "CoinGecko", TimestampUtc = ts });
        db.PriceSnapshots.Add(new PriceSnapshot { Asset = asset, PriceUsd = 2m, Source = "CoinGecko", TimestampUtc = ts });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task NegativeQuantity_IsRejectedByCheckConstraint()
    {
        await using var testDb = await TestDatabase.CreateAsync();
        await using var db = testDb.CreateContext();

        var asset = new Asset { Name = "Test", Symbol = "TST", CoinGeckoId = "test" };
        db.Positions.Add(new Position { Asset = asset, WalletOrExchangeName = "W", Quantity = -1m });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
