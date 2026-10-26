using CryptoPortfolioManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

/// <summary>
/// Inserts fictitious data for the test database and automated tests; the application itself never calls this.
/// Does nothing when assets already exist. Without <paramref name="includePrices"/> only assets and positions are
/// inserted, so all prices in the database come from a real sync.
/// </summary>
public static class SampleDataSeeder
{
    public static async Task SeedAsync(AppDbContext db, DateTime? nowUtc = null, bool includePrices = true, CancellationToken ct = default)
    {
        if (await db.Assets.AnyAsync(ct)) return;

        var now = nowUtc ?? DateTime.UtcNow;

        var btc = new Asset { Name = "Bitcoin", Symbol = "BTC", CoinGeckoId = "bitcoin" };
        var eth = new Asset { Name = "Ethereum", Symbol = "ETH", CoinGeckoId = "ethereum" };
        var usdc = new Asset
        {
            Name = "USD Coin", Symbol = "USDC", CoinGeckoId = "usd-coin",
            ContractChain = Chain.Ethereum,
            ContractAddress = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48",
            TokenDecimals = 6
        };

        db.Assets.AddRange(btc, eth, usdc);

        db.Positions.AddRange(
            new Position
            {
                Asset = btc, WalletOrExchangeName = "Ledger", SourceType = PositionSourceType.OnChainWallet,
                Chain = Chain.Bitcoin, WalletAddress = "bc1qexampleexampleexampleexampleexample0",
                Quantity = 0.25m, Notes = "Fictief voorbeeld"
            },
            new Position
            {
                Asset = btc, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange,
                Quantity = 0.1m
            },
            new Position
            {
                Asset = eth, WalletOrExchangeName = "Ledger", SourceType = PositionSourceType.OnChainWallet,
                Chain = Chain.Ethereum, WalletAddress = "0x0000000000000000000000000000000000000001",
                Quantity = 3.44m
            },
            new Position
            {
                Asset = usdc, WalletOrExchangeName = "MetaMask", SourceType = PositionSourceType.OnChainWallet,
                Chain = Chain.Ethereum, WalletAddress = "0x0000000000000000000000000000000000000001",
                Quantity = 1250.5m
            },
            new Position
            {
                Asset = eth, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange,
                Quantity = 2.41m
            });

        if (!includePrices)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        // Three daily snapshots per asset, newest last.
        var prices = new (Asset Asset, decimal[] Usd)[]
        {
            (btc, [60000m, 61250.5m, 62000m]),
            (eth, [2400m, 2450.25m, 2500m]),
            (usdc, [1m, 1.0001m, 0.9999m])
        };

        foreach (var (asset, usd) in prices)
            for (var i = 0; i < usd.Length; i++)
                db.PriceSnapshots.Add(new PriceSnapshot
                {
                    Asset = asset, PriceUsd = usd[i], Source = "Sample",
                    TimestampUtc = now.AddDays(i - (usd.Length - 1))
                });

        db.PriceUpdateLogs.AddRange(
            new PriceUpdateLog
            {
                StartedAtUtc = now.AddDays(-1), CompletedAtUtc = now.AddDays(-1).AddSeconds(3),
                Status = PriceUpdateStatus.Succeeded
            },
            new PriceUpdateLog
            {
                StartedAtUtc = now, CompletedAtUtc = now.AddSeconds(1),
                Status = PriceUpdateStatus.Failed, ErrorMessage = "Fictieve fout: prijsbron niet bereikbaar"
            });

        await db.SaveChangesAsync(ct);
    }
}
