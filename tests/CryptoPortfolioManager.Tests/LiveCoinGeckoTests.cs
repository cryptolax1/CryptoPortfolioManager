using CryptoPortfolioManager.Core;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure;
using CryptoPortfolioManager.Infrastructure.CoinGecko;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPortfolioManager.Tests;

/// <summary>
/// Calls the real CoinGecko Demo API and stores the result in a temporary copy of the test data.
/// Opt-in because it uses a few API calls from the monthly quota:
/// set COINGECKO_LIVE_TESTS=1 and store the key in User Secrets (CoinGecko:ApiKey).
/// </summary>
public class LiveCoinGeckoTests
{
    [Fact]
    public async Task Sync_FetchesRealPrices_AndStoresThemWithoutDuplicates()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveCoinGeckoTests>()
            .AddEnvironmentVariables()
            .Build();

        Assert.SkipUnless(Environment.GetEnvironmentVariable("COINGECKO_LIVE_TESTS") == "1",
            "Set COINGECKO_LIVE_TESTS=1 to run the live API test.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(config["CoinGecko:ApiKey"]),
            "No CoinGecko:ApiKey in User Secrets.");

        var path = Path.Combine(Path.GetTempPath(), $"cpm-live-{Guid.NewGuid():N}.test.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCore();
            services.AddInfrastructure(DatabasePaths.ConnectionString(path));
            services.AddCoinGeckoPriceProvider(config);
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.MigrateAsync();
                await SampleDataSeeder.SeedAsync(db, includePrices: false);
            }

            async Task<PriceSyncResult> SyncAsync()
            {
                await using var scope = provider.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IPriceSyncService>().SyncAsync();
            }

            var first = await SyncAsync();
            Assert.Equal(PriceUpdateStatus.Succeeded, first.Status);
            Assert.True(first.HistoricalSnapshotsStored > 0);

            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var snapshots = await db.PriceSnapshots.ToListAsync();
                Assert.All(snapshots, s => { Assert.Equal("CoinGecko", s.Source); Assert.True(s.PriceUsd > 0); });
                Assert.Equal(await db.Assets.CountAsync(), snapshots.Select(s => s.AssetId).Distinct().Count());

                var overview = await scope.ServiceProvider.GetRequiredService<IPortfolioService>().GetPortfolioOverviewAsync();
                Assert.All(overview.Assets, a =>
                {
                    Assert.NotNull(a.PriceUsd);
                    Assert.NotNull(a.DeltaFor(DeltaPeriod.OneDay));
                    Assert.NotNull(a.DeltaFor(DeltaPeriod.OneWeek));
                });
            }

            var second = await SyncAsync();
            Assert.Equal(0, second.HistoricalSnapshotsStored);

            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var duplicates = await db.PriceSnapshots
                    .GroupBy(s => new { s.AssetId, s.Source, s.TimestampUtc })
                    .CountAsync(g => g.Count() > 1);
                Assert.Equal(0, duplicates);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
