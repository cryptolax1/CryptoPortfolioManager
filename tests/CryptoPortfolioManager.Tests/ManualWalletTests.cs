using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;

namespace CryptoPortfolioManager.Tests;

public class ManualWalletTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoReader : IWalletBalanceReader
    {
        public int Calls { get; private set; }
        public bool Supports(Chain chain) => true;
        public Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(0m);
        }
    }

    [Fact]
    public async Task ManualHoldings_KeepZeroHistoryAndTimestamp_ThroughScansAndWhitelistRemoval()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var ctx = db.CreateContext()) await DefaultReferenceData.EnsureAsync(ctx, Ct);
        var repo = new WalletRepository(db.CreateFactory());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var scanLock = new WalletScanLock();
        var wallets = new WalletService(repo, scanLock, clock);
        var scan = new WalletScanService(repo, new NoReader(), [], scanLock, clock);
        Assert.True((await wallets.AddAsync(new WalletInput("", "Manual A", WalletType.Manual, ""), Ct)).Ok);
        Assert.True((await wallets.AddAsync(new WalletInput("", "Manual B", WalletType.Manual, ""), Ct)).Ok);
        var wallet = (await wallets.GetWalletsAsync(Ct)).Single(w => w.Name == "Manual A");
        var token = (await repo.GetWhitelistAsync(Ct)).First(t => t.Symbol == "CRO");
        Assert.False((await wallets.SetManualQuantityAsync(wallet.Id, token.AssetId, -1m, Ct)).Ok);
        Assert.False((await wallets.SetManualQuantityAsync(wallet.Id, int.MaxValue, 1m, Ct)).Ok);
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, token.AssetId, 5m, Ct)).Ok);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, token.AssetId, 5m, Ct)).Ok);
        var unchanged = (await wallets.GetWalletPositionsAsync(Ct)).Single();
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(-1), unchanged.LastQuantityChangedAtUtc);
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, token.AssetId, 0m, Ct)).Ok);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await wallets.UpdateAsync(wallet.Id, new WalletInput("", "Renamed", WalletType.Manual, ""), Ct)).Ok);
        var position = Assert.Single(await wallets.GetWalletPositionsAsync(Ct));
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(-1), position.LastQuantityChangedAtUtc);
        Assert.Equal("Renamed", position.WalletOrExchangeName);

        var individual = await scan.ScanWalletAsync(wallet.Id, Ct);
        Assert.Contains(individual.Errors, e => e.Contains("manual"));
        await repo.DeleteWhitelistedTokenAsync(token.Id, Ct);
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, token.AssetId, 2m, Ct)).Ok);
        Assert.Single(await wallets.GetWalletPositionsAsync(Ct));
        await using var check = db.CreateContext();
        var logs = await check.PositionChangeLogs.OrderBy(l => l.Id).ToListAsync(Ct);
        Assert.Equal(new decimal?[] { 5m, 0m, 2m }, logs.Select(l => l.NewQuantity));
        Assert.All(logs, l => Assert.Equal(wallet.Id, l.WalletId));
        Assert.Single(await check.Positions.Where(p => p.WalletId == wallet.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task ManualWallets_AreSkippedByFullScan_AndZeroBalanceAppearsInPortfolio()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var ctx = db.CreateContext()) await DefaultReferenceData.EnsureAsync(ctx, Ct);
        var repo = new WalletRepository(db.CreateFactory());
        var clock = new FakeTimeProvider();
        var scanLock = new WalletScanLock();
        var wallets = new WalletService(repo, scanLock, clock);
        Assert.True((await wallets.AddAsync(new WalletInput("", "Manual", WalletType.Manual, ""), Ct)).Ok);
        var wallet = (await wallets.GetWalletsAsync(Ct)).Single(w => w.Type == WalletType.Manual);
        var assetId = (await repo.GetWhitelistAsync(Ct)).First().AssetId;
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, assetId, 0m, Ct)).Ok);
        var reader = new NoReader();
        var scan = new WalletScanService(repo, reader, [], scanLock, clock);
        var result = await scan.ScanAsync(Ct);
        Assert.Equal(2, result.WalletsScanned);
        Assert.Equal(10, reader.Calls);
        Assert.Empty(result.Errors);
        var portfolio = new PortfolioService(new PortfolioRepository(db.CreateFactory()), clock);
        Assert.Equal(0m, Assert.Single((await portfolio.GetPortfolioOverviewAsync(Ct)).Assets).Quantity);
        Assert.Single(await wallets.GetWalletPositionsAsync(Ct));
    }

    [Fact]
    public async Task Delete_RequiresFullyPricedTotalBelowTen_AndRestorePreservesHoldings()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var ctx = db.CreateContext()) await DefaultReferenceData.EnsureAsync(ctx, Ct);
        var repo = new WalletRepository(db.CreateFactory());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var wallets = new WalletService(repo, new WalletScanLock(), clock);
        Assert.True((await wallets.AddAsync(new WalletInput("", "Manual", WalletType.Manual, ""), Ct)).Ok);
        var wallet = (await wallets.GetWalletsAsync(Ct)).Single(w => w.Type == WalletType.Manual);
        var ids = (await repo.GetWhitelistAsync(Ct)).Select(t => t.AssetId).Distinct().Take(2).ToArray();
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, ids[0], 1m, Ct)).Ok);
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, ids[1], 1m, Ct)).Ok);
        await using (var ctx = db.CreateContext())
        {
            ctx.PriceSnapshots.Add(new PriceSnapshot { AssetId = ids[0], PriceUsd = 9m, TimestampUtc = clock.GetUtcNow().UtcDateTime, Source = "Test" });
            await ctx.SaveChangesAsync(Ct);
        }
        Assert.Contains("price", (await wallets.DeleteAsync(wallet.Id, Ct)).Error);
        await using (var ctx = db.CreateContext())
        {
            ctx.PriceSnapshots.Add(new PriceSnapshot { AssetId = ids[1], PriceUsd = 1m, TimestampUtc = clock.GetUtcNow().UtcDateTime, Source = "Test" });
            await ctx.SaveChangesAsync(Ct);
        }
        Assert.Contains("less than $10", (await wallets.DeleteAsync(wallet.Id, Ct)).Error);
        Assert.True((await wallets.SetManualQuantityAsync(wallet.Id, ids[1], 0m, Ct)).Ok);
        Assert.True((await wallets.DeleteAsync(wallet.Id, Ct)).Ok);
        Assert.DoesNotContain(await wallets.GetWalletsAsync(Ct), w => w.Id == wallet.Id);
        Assert.Contains(await wallets.GetArchivedWalletsAsync(Ct), w => w.Id == wallet.Id);
        Assert.Empty(await wallets.GetWalletPositionsAsync(Ct));
        var portfolio = new PortfolioService(new PortfolioRepository(db.CreateFactory()), clock);
        Assert.Empty((await portfolio.GetPortfolioOverviewAsync(Ct)).Assets);
        Assert.False((await wallets.SetManualQuantityAsync(wallet.Id, ids[0], 4m, Ct)).Ok);
        Assert.True((await wallets.RestoreAsync(wallet.Id, Ct)).Ok);
        Assert.Equal(2, (await wallets.GetWalletPositionsAsync(Ct)).Count);
        Assert.Equal(2, (await portfolio.GetPortfolioOverviewAsync(Ct)).Assets.Count);
    }

    [Fact]
    public async Task ArchivedScannedPositions_SurviveWhitelistRemovalAndCanBeRestored()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var ctx = db.CreateContext()) await DefaultReferenceData.EnsureAsync(ctx, Ct);
        var repo = new WalletRepository(db.CreateFactory());
        var clock = new FakeTimeProvider();
        var wallets = new WalletService(repo, new WalletScanLock(), clock);
        var wallet = (await wallets.GetWalletsAsync(Ct)).First();
        var token = (await repo.GetWhitelistAsync(Ct)).First();
        await repo.ApplyScanAsync(wallet.Id, [new TokenBalance(token.Id, 1m)], null, clock.GetUtcNow().UtcDateTime, [], Ct);
        await using (var ctx = db.CreateContext())
        {
            ctx.PriceSnapshots.Add(new PriceSnapshot { AssetId = token.AssetId, PriceUsd = 9.99m, TimestampUtc = clock.GetUtcNow().UtcDateTime, Source = "Test" });
            await ctx.SaveChangesAsync(Ct);
        }
        Assert.True((await wallets.DeleteAsync(wallet.Id, Ct)).Ok);
        await repo.DeleteWhitelistedTokenAsync(token.Id, Ct);
        await using (var ctx = db.CreateContext())
            Assert.Single(await ctx.Positions.Where(p => p.WalletId == wallet.Id).ToListAsync(Ct));
        Assert.True((await wallets.RestoreAsync(wallet.Id, Ct)).Ok);
        Assert.Single(await wallets.GetWalletPositionsAsync(Ct));
    }

    [Fact]
    public async Task Migration_PreservesExistingWalletAndScanTime()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261026003435_WiderWalletAddress", Ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Wallets (Address, Name, Type, Source, CreatedAtUtc, LastScannedAtUtc) VALUES ('', 'Bitvavo', 'Cex', 'Bitvavo', '2026-10-01 00:00:00', '2026-10-29 12:00:00')", Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        var wallet = await db.Wallets.SingleAsync(Ct);
        Assert.False(wallet.IsArchived);
        Assert.Equal(new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), wallet.LastScannedAtUtc);
        db.Wallets.Add(new Wallet { Address = "", Name = "Manual", Type = WalletType.Manual, Source = "Manual" });
        await db.SaveChangesAsync(Ct);
        Assert.Equal(2, await db.Wallets.CountAsync(Ct));
    }
}
