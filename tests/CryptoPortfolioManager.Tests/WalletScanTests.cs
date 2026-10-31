using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace CryptoPortfolioManager.Tests;

public class WalletScanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AddressA = "0x59711D16ff7F32EAe184BD1B892C7e6d6C058bFc";
    private const string AddressB = "0x1ED7E061015b62a1D57266cFd2ef3bE8F96FF8e3";
    private const string UsdcCronos = "0xc21223249CA28397B4B6541dfFaEcC539BfF0c59";

    private sealed class FakeReader : IWalletBalanceReader
    {
        public Dictionary<(string Wallet, Chain Chain, string? Contract), object> Balances { get; } = new();
        public List<(string Wallet, Chain Chain, string? Contract)> Calls { get; } = [];
        public TaskCompletionSource? Gate { get; set; }

        public bool Supports(Chain chain) => chain is Chain.Cronos or Chain.BnbSmartChain or Chain.Scroll or Chain.Solana or Chain.XrpLedger;

        public async Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default)
        {
            Calls.Add((walletAddress, chain, contractAddress));
            if (Gate is not null) await Gate.Task;

            if (!Balances.TryGetValue((walletAddress, chain, contractAddress), out var value)) return 0m;
            return value is Exception ex ? throw ex : (decimal)value;
        }
    }

        private sealed class FakeExchange : IExchangeBalanceReader
    {
        public string Name { get; init; } = ExchangeCatalog.Bitvavo;
        public bool IsConfigured { get; set; } = true;
        public List<ExchangePosition> Holdings { get; } = [];
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ExchangePosition>> GetBalancesAsync(CancellationToken ct = default)
        {
            Calls++;
            return Failure is not null
                ? Task.FromException<IReadOnlyList<ExchangePosition>>(Failure)
                : Task.FromResult<IReadOnlyList<ExchangePosition>>(Holdings);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required WalletRepository Repository { get; init; }
        public required FakeReader Reader { get; init; }
        public required FakeExchange Exchange { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required WalletScanService Scan { get; init; }
        public WalletService Wallets => new(Repository);
        public WhitelistService Whitelist => new(Repository, Reader);

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static async Task<Fixture> CreateAsync(bool defaults = true)
        {
            var db = await TestDatabase.CreateAsync();
            if (defaults)
            {
                await using var ctx = db.CreateContext();
                await DefaultReferenceData.EnsureAsync(ctx, Ct);
            }

            var repository = new WalletRepository(db.CreateFactory());
            var reader = new FakeReader();
            var exchange = new FakeExchange();
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
            return new Fixture
            {
                Db = db, Repository = repository, Reader = reader, Exchange = exchange, Clock = clock,
                Scan = new WalletScanService(repository, reader, [exchange], new WalletScanLock(), clock)
            };
        }

        public async Task<List<Position>> PositionsAsync()
        {
            await using var ctx = Db.CreateContext();
            return await ctx.Positions.Include(p => p.Asset).OrderBy(p => p.Id).ToListAsync(Ct);
        }
    }

    // ---- default data -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultData_ContainsRequestedWalletsAndWhitelist()
    {
        await using var f = await Fixture.CreateAsync();
        await using var ctx = f.Db.CreateContext();

        var wallets = await ctx.Wallets.OrderBy(w => w.Name).ToListAsync(Ct);
        Assert.Equal(["Cronos Main", "Thor Vapor"], wallets.Select(w => w.Name));
        Assert.All(wallets, w => { Assert.Equal(WalletType.Hot, w.Type); Assert.Equal("Metamask", w.Source); });
        Assert.Equal(AddressA, wallets[0].Address);
        Assert.Equal(AddressB, wallets[1].Address);

        var tokens = await ctx.WhitelistedTokens.Include(t => t.Asset).ToListAsync(Ct);
        Assert.Equal(
            ["BnbSmartChain:BNB", "BnbSmartChain:USDC", "BnbSmartChain:XRP", "Cronos:CRO", "Cronos:USDC"],
            tokens.Select(t => $"{t.Chain}:{t.Symbol}").Order());

        Assert.Null(tokens.Single(t => t.Symbol == "CRO").ContractAddress);
        Assert.Null(tokens.Single(t => t.Symbol == "BNB").ContractAddress);
        Assert.Equal(6, tokens.Single(t => t.Symbol == "USDC" && t.Chain == Chain.Cronos).Decimals);
        Assert.Equal(18, tokens.Single(t => t.Symbol == "USDC" && t.Chain == Chain.BnbSmartChain).Decimals);

        // USDC on both chains points at one asset, so it is priced and summed once.
        Assert.Single(tokens.Where(t => t.Symbol == "USDC").Select(t => t.AssetId).Distinct());
    }

    [Fact]
    public async Task DefaultData_IsInsertedOnlyOnce_AndReusesExistingAssets()
    {
        await using var db = await TestDatabase.CreateAsync(seed: true); // already has USD Coin
        await using var ctx = db.CreateContext();

        Assert.True(await DefaultReferenceData.EnsureAsync(ctx, Ct));
        Assert.False(await DefaultReferenceData.EnsureAsync(ctx, Ct));

        Assert.Equal(1, await ctx.Assets.CountAsync(a => a.CoinGeckoId == "usd-coin", Ct));
        Assert.Equal(2, await ctx.Wallets.CountAsync(Ct));
        Assert.Equal(5, await ctx.WhitelistedTokens.CountAsync(Ct));
    }

    // ---- scan ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scan_CreatesPositionsForWhitelistedTokensWithBalance()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 9855.5m;
        f.Reader.Balances[(AddressA, Chain.Cronos, UsdcCronos)] = 12.34m;
        f.Reader.Balances[(AddressB, Chain.BnbSmartChain, null)] = 0.75m;

        var result = await f.Scan.ScanAsync(Ct);

        Assert.False(result.AlreadyRunning);
        Assert.Equal(2, result.WalletsScanned);
        Assert.Equal(new PositionChanges(3, 0, 0), result.Changes);
        Assert.Empty(result.Errors);
        Assert.Equal(10, f.Reader.Calls.Count); // 2 wallets x 5 whitelisted tokens

        var positions = await f.PositionsAsync();
        Assert.Equal(3, positions.Count);

        var cro = positions.Single(p => p.Asset!.Symbol == "CRO");
        Assert.Equal(9855.5m, cro.Quantity);
        Assert.Equal(AddressA, cro.WalletAddress);
        Assert.Equal("Cronos Main", cro.WalletOrExchangeName);
        Assert.Equal(Chain.Cronos, cro.Chain);
        Assert.Equal(PositionSourceType.OnChainWallet, cro.SourceType);
        Assert.NotNull(cro.WalletId);
        Assert.Equal(new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc), cro.LastSyncedAtUtc);

        Assert.Equal("Thor Vapor", positions.Single(p => p.Asset!.Symbol == "BNB").WalletOrExchangeName);
    }

    [Fact]
    public async Task Scan_UpdatesChangedBalance_RemovesZeroBalance_AndIsIdempotent()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        f.Reader.Balances[(AddressA, Chain.Cronos, UsdcCronos)] = 5m;
        await f.Scan.ScanAsync(Ct);

        var repeat = await f.Scan.ScanAsync(Ct);
        Assert.Equal(new PositionChanges(0, 0, 0), repeat.Changes);

        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 120.25m;
        f.Reader.Balances[(AddressA, Chain.Cronos, UsdcCronos)] = 0m;
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(new PositionChanges(0, 1, 1), result.Changes);
        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal(120.25m, position.Quantity);
    }

    [Fact]
    public async Task Scan_FailedRead_KeepsStoredPosition_AndReportsError()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);

        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = new WalletBalanceException("The request timed out.");
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(new PositionChanges(0, 0, 0), result.Changes);
        Assert.Contains("Cronos Main: CRO on Cronos: The request timed out.", result.Errors.Single());
        Assert.Equal(100m, Assert.Single(await f.PositionsAsync()).Quantity);

        await using var ctx = f.Db.CreateContext();
        var wallet = await ctx.Wallets.SingleAsync(w => w.Name == "Cronos Main", Ct);
        Assert.Contains("timed out", wallet.LastScanError);
        Assert.Null((await ctx.Wallets.SingleAsync(w => w.Name == "Thor Vapor", Ct)).LastScanError);
    }

    [Fact]
    public async Task Scan_RecordsLastScanTime_OnlyForCompleteSuccess()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Scan.ScanAsync(Ct);

        await using (var ctx = f.Db.CreateContext())
            Assert.All(await ctx.Wallets.ToListAsync(Ct), w => Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, w.LastScannedAtUtc));

        f.Clock.Advance(TimeSpan.FromHours(1));
        f.Reader.Balances[(AddressB, Chain.BnbSmartChain, null)] = new WalletBalanceException("boom");
        await f.Scan.ScanAsync(Ct);

        await using var after = f.Db.CreateContext();
        var a = await after.Wallets.SingleAsync(w => w.Address == AddressA, Ct);
        var b = await after.Wallets.SingleAsync(w => w.Address == AddressB, Ct);
        Assert.Equal(new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc), a.LastScannedAtUtc);
        Assert.Equal(new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc), b.LastScannedAtUtc); // last complete success
        Assert.NotNull(b.LastScanError);
    }

    [Fact]
    public async Task Scan_RemovesPositionsNoWalletOwns_AndKeepsThemInTheLog()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var ctx = f.Db.CreateContext())
        {
            var croAssetId = (await ctx.Assets.SingleAsync(a => a.Symbol == "CRO", Ct)).Id;
            ctx.Positions.Add(new Position
            {
                AssetId = croAssetId, WalletOrExchangeName = "Handmatig", SourceType = PositionSourceType.Manual, Quantity = 7m
            });
            await ctx.SaveChangesAsync(Ct);
        }

        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(1, result.Changes.Removed);
        var current = Assert.Single(await f.PositionsAsync());
        Assert.Equal(100m, current.Quantity);

        await using var check = f.Db.CreateContext();
        var log = await check.PositionChangeLogs.OrderBy(l => l.Id).ToListAsync(Ct);
        Assert.Contains(log, l => l is { ChangeType: PositionChangeType.Added, NewQuantity: 100m, AssetSymbol: "CRO" });
        Assert.Contains(log, l => l is
            { ChangeType: PositionChangeType.Removed, PreviousQuantity: 7m, WalletOrExchangeName: "Handmatig", NewQuantity: null });
    }

    [Fact]
    public async Task ScanWallet_ReadsOnlyThatWallet_AndKeepsOtherPositions()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        f.Reader.Balances[(AddressB, Chain.Cronos, null)] = 50m;
        await f.Scan.ScanAsync(Ct);

        var wallets = await f.Wallets.GetWalletsAsync(Ct);
        var walletA = wallets.Single(w => w.Address == AddressA);
        f.Reader.Calls.Clear();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 120m;
        f.Reader.Balances[(AddressB, Chain.Cronos, null)] = 999m;

        var result = await f.Scan.ScanWalletAsync(walletA.Id, Ct);

        Assert.Equal(1, result.WalletsScanned);
        Assert.All(f.Reader.Calls, c => Assert.Equal(AddressA, c.Wallet));
        var positions = await f.PositionsAsync();
        Assert.Equal(120m, positions.Single(p => p.WalletAddress == AddressA && p.Chain == Chain.Cronos).Quantity);
        Assert.Equal(50m, positions.Single(p => p.WalletAddress == AddressB && p.Chain == Chain.Cronos).Quantity);
    }

    [Fact]
    public async Task ScanWallet_UnknownWallet_ReportsError()
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Scan.ScanWalletAsync(9999, Ct);
        Assert.Equal(0, result.WalletsScanned);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Scan_WithReadErrors_KeepsUnownedPositions()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var ctx = f.Db.CreateContext())
        {
            var croAssetId = (await ctx.Assets.SingleAsync(a => a.Symbol == "CRO", Ct)).Id;
            ctx.Positions.Add(new Position
            {
                AssetId = croAssetId, WalletOrExchangeName = "Handmatig", SourceType = PositionSourceType.Manual, Quantity = 7m
            });
            await ctx.SaveChangesAsync(Ct);
        }

        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = new WalletBalanceException("rpc down");
        var result = await f.Scan.ScanAsync(Ct);

        Assert.NotEmpty(result.Errors);
        Assert.Single(await f.PositionsAsync());
    }

    [Fact]
    public async Task Scan_LogsQuantityChangesAndZeroBalances()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 60m;
        await f.Scan.ScanAsync(Ct);
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 0m;
        await f.Scan.ScanAsync(Ct);

        Assert.Empty(await f.PositionsAsync());
        await using var check = f.Db.CreateContext();
        var log = await check.PositionChangeLogs.OrderBy(l => l.Id).ToListAsync(Ct);
        Assert.Equal(
            [(PositionChangeType.Added, (decimal?)null, (decimal?)100m), (PositionChangeType.Updated, 100m, 60m), (PositionChangeType.Removed, 60m, null)],
            log.Select(l => (l.ChangeType, l.PreviousQuantity, l.NewQuantity)).ToArray());
    }

    [Fact]
    public async Task DeletingWallet_LogsItsPositions()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);

        var wallet = (await f.Wallets.GetWalletsAsync(Ct)).Single(w => w.Address == AddressA);
        await f.Wallets.DeleteAsync(wallet.Id, Ct);

        await using var check = f.Db.CreateContext();
        Assert.Contains(await check.PositionChangeLogs.ToListAsync(Ct), l => l is { ChangeType: PositionChangeType.Removed, Reason: "Wallet deleted" });
    }

        [Fact]
    public async Task Scan_Exchange_WritesWhitelistedHoldings_AndIncludesMoneyInOpenOrders()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "USD Coin", "USDC", Chain.Cronos, UsdcCronos, 6, "usd-coin"), Ct)).Ok);

        // The client already splits "available" + "inOrder" into one quantity per ticker.
        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 42.5m));
        f.Exchange.Holdings.Add(new ExchangePosition("USDC", "USDC", "exchange-usdc", null, null, 10m));

        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(1, result.WalletsScanned);
        Assert.Equal(0, result.WalletsSkipped);
        Assert.Empty(result.Errors);
        Assert.Equal(new PositionChanges(2, 0, 0), result.Changes);

        var positions = await f.PositionsAsync();
        var cro = positions.Single(p => p.Asset!.Symbol == "CRO");
        Assert.Equal(42.5m, cro.Quantity);
        Assert.Equal(PositionSourceType.Exchange, cro.SourceType);
        Assert.Equal(Chain.Cronos, cro.Chain);
        Assert.Null(cro.WalletAddress);
        Assert.Equal("Bitvavo", cro.WalletOrExchangeName);
        Assert.NotNull(cro.WalletId);
        Assert.Equal(10m, positions.Single(p => p.Asset!.Symbol == "USDC").Quantity);

        // The exchange was never read through the on-chain reader.
        Assert.Empty(f.Reader.Calls);
    }

    [Fact]
    public async Task Scan_Exchange_RemovesWhitelistedHoldingThatIsGone_AndKeepsTheRest()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "USD Coin", "USDC", Chain.Cronos, UsdcCronos, 6, "usd-coin"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 40m));
        f.Exchange.Holdings.Add(new ExchangePosition("USDC", "USDC", "exchange-usdc", null, null, 10m));
        await f.Scan.ScanAsync(Ct);

        // The exchange now reports only CRO: the USDC balance is gone, so its position goes too.
        f.Exchange.Holdings.Clear();
        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 0m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(new PositionChanges(0, 0, 2), result.Changes);
        Assert.Empty(await f.PositionsAsync());
        await using var check = f.Db.CreateContext();
        Assert.Equal(2, await check.PositionChangeLogs.CountAsync(l => l.Reason == "Exchange scan" && l.ChangeType == PositionChangeType.Removed, Ct));
    }

        [Fact]
    public async Task Scan_Exchange_OnlyWhitelistedHoldings_AreShown()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 5m));
        f.Exchange.Holdings.Add(new ExchangePosition("ETH", "Ethereum", "exchange-eth", null, null, 0.25m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Empty(result.Errors);

        // Only the whitelisted CRO is shown; the un-whitelisted ETH (and anything else) is not.
        var cro = Assert.Single(await f.PositionsAsync());
        Assert.Equal("CRO", cro.Asset!.Symbol);
        Assert.Equal("crypto-com-chain", cro.Asset!.CoinGeckoId);
        Assert.Equal(Chain.Cronos, cro.Chain);

        await using var check = f.Db.CreateContext();
        Assert.False(await check.Assets.AnyAsync(a => a.Symbol == "ETH", Ct));
    }

                [Fact]
    public async Task Scan_Exchange_ShowsFiatEuro_ShownAsEur_ButPricedThroughEuroCoin()
    {
        // Bitvavo's /v2/balance also lists an EUR cash deposit. It is shown as EUR (the exchange's ticker), while the
        // price comes from the real CoinGecko coin "euro-coin". Other un-whitelisted coins are not shown at all.
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 1234.56m));
        f.Exchange.Holdings.Add(new ExchangePosition("BTC", "BTC", ExchangePosition.PlaceholderId("BTC"), null, null, 0.5m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Empty(result.Errors);
        var position = Assert.Single(await f.PositionsAsync());

        // Shown as EUR, but priced through euro-coin.
        Assert.Equal("EUR", position.Asset!.Symbol);
        Assert.Equal("EUR", position.Asset!.Name);
        Assert.Equal("euro-coin", position.Asset!.CoinGeckoId);
        Assert.Equal(PositionSourceType.Exchange, position.SourceType);
        Assert.Null(position.Chain);
        Assert.Equal(1234.56m, position.Quantity);

        // The un-whitelisted BTC holding is not shown.
        await using var check = f.Db.CreateContext();
        Assert.False(await check.Assets.AnyAsync(a => a.Symbol == "BTC", Ct));
    }

    [Fact]
    public async Task Scan_Exchange_ExistingEurPlaceholder_IsRepricedInPlace_InsteadOfDuplicated()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        // Simulate a scan from before this change: EUR was stored as a price-less "exchange-eur" asset with a position.
        int placeholderAssetId;
        await using (var ctx = f.Db.CreateContext())
        {
            var placeholder = new Asset { Name = "EUR", Symbol = "EUR", CoinGeckoId = ExchangePosition.PlaceholderId("EUR") };
            ctx.Assets.Add(placeholder);
            await ctx.SaveChangesAsync(Ct);
            placeholderAssetId = placeholder.Id;

            var walletId = (await ctx.Wallets.SingleAsync(Ct)).Id;
            ctx.Positions.Add(new Position
            {
                AssetId = placeholderAssetId, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange,
                Chain = null, WalletAddress = null, Quantity = 100m, WalletId = walletId
            });
            await ctx.SaveChangesAsync(Ct);
        }

        // The next scan sees the EUR balance and must re-price that exact asset rather than add a second one.
        f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 100m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(new PositionChanges(0, 0, 0), result.Changes); // same asset, same quantity: nothing to add or drop
        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal(placeholderAssetId, position.AssetId); // the placeholder row was adopted, not duplicated
        Assert.Equal("euro-coin", position.Asset!.CoinGeckoId);

                await using var check = f.Db.CreateContext();
                Assert.Equal(1, await check.Assets.CountAsync(Ct)); // no orphan placeholder left behind
    }

            [Fact]
    public async Task Scan_Exchange_Rescan_RepricesEuro_AndDropsUnwhitelistedSei()
    {
        // Mirrors a real database from before this change: both an "exchange-eur" and an "exchange-sei" placeholder
        // asset exist with a position. On the next scan EUR must become priceable (euro-coin, still shown as EUR) while
        // SEI, not being whitelisted, is removed from the view.
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        await using (var ctx = f.Db.CreateContext())
        {
            var walletId = (await ctx.Wallets.SingleAsync(Ct)).Id;
            var eur = new Asset { Name = "EUR", Symbol = "EUR", CoinGeckoId = ExchangePosition.PlaceholderId("EUR") };
            var sei = new Asset { Name = "SEI", Symbol = "SEI", CoinGeckoId = ExchangePosition.PlaceholderId("SEI") };
            ctx.Assets.AddRange(eur, sei);
            await ctx.SaveChangesAsync(Ct);
            ctx.Positions.AddRange(
                new Position { AssetId = eur.Id, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange, Quantity = 50m, WalletId = walletId },
                new Position { AssetId = sei.Id, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange, Quantity = 30m, WalletId = walletId });
            await ctx.SaveChangesAsync(Ct);
        }

        // The exchange reports both holdings again on the re-scan.
        f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 50m));
        f.Exchange.Holdings.Add(new ExchangePosition("SEI", "SEI", ExchangePosition.PlaceholderId("SEI"), null, null, 30m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(1, result.Changes.Removed); // the un-whitelisted SEI position is dropped

        // EUR was re-priced in place: still shown as EUR, now priced through euro-coin.
        var eurPosition = Assert.Single(await f.PositionsAsync());
        Assert.Equal("EUR", eurPosition.Asset!.Symbol);
        Assert.Equal("euro-coin", eurPosition.Asset!.CoinGeckoId);
    }

            [Fact]
            public async Task Scan_Exchange_ExistingEurcAsset_IsRenamedToEur_InPlace()
            {
                // An earlier build stored the euro as an asset named after its CoinGecko coin ("EURC"). A re-scan must rename
                // it to the exchange's own ticker (EUR) in place, keeping the same asset and its position - never "EURC".
                await using var f = await Fixture.CreateAsync(defaults: false);
                Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

                int assetId;
                await using (var ctx = f.Db.CreateContext())
                {
                    var eurc = new Asset { Name = "EURC", Symbol = "EURC", CoinGeckoId = "euro-coin" };
                    ctx.Assets.Add(eurc);
                    await ctx.SaveChangesAsync(Ct);
                    assetId = eurc.Id;

                    var walletId = (await ctx.Wallets.SingleAsync(Ct)).Id;
                    ctx.Positions.Add(new Position
                    {
                        AssetId = assetId, WalletOrExchangeName = "Bitvavo", SourceType = PositionSourceType.Exchange,
                        Chain = null, WalletAddress = null, Quantity = 100m, WalletId = walletId
                    });
                    await ctx.SaveChangesAsync(Ct);
                }

                f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 100m));
                var result = await f.Scan.ScanAsync(Ct);

                Assert.Equal(new PositionChanges(0, 0, 0), result.Changes); // same asset, same quantity
                var position = Assert.Single(await f.PositionsAsync());
                Assert.Equal(assetId, position.AssetId); // renamed in place, not replaced
                Assert.Equal("EUR", position.Asset!.Symbol);
                Assert.Equal("EUR", position.Asset!.Name);
                Assert.Equal("euro-coin", position.Asset!.CoinGeckoId);

                await using var check = f.Db.CreateContext();
                Assert.Equal(1, await check.Assets.CountAsync(Ct)); // no second asset named EUR was added
                Assert.False(await check.Assets.AnyAsync(a => a.Symbol == "EURC", Ct));
            }

            [Fact]
            public async Task Scan_Exchange_DoesNotCreateAnAssetForZeroFiatBalance()
    {
        // A zero EUR balance is dropped like any other: no euro-coin asset is created for nothing.
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 0m));
        await f.Scan.ScanAsync(Ct);

                Assert.Empty(await f.PositionsAsync());
        await using var check = f.Db.CreateContext();
        Assert.False(await check.Assets.AnyAsync(a => a.CoinGeckoId == "euro-coin", Ct));
    }

    [Fact]
    public async Task Scan_Exchange_StrayFiatWhitelistRow_IsHidden_AndDoesNotDuplicateEur()
    {
        // EUR cash is an internal scan mechanic, but an old database may still carry a manual "EUR" whitelist entry.
        // It must not appear in the whitelist and must not create a second EUR position next to the fiat one.
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        await using (var ctx = f.Db.CreateContext())
        {
            var asset = new Asset { Name = "EUR", Symbol = "EUR", CoinGeckoId = "euro-coin" };
            ctx.Assets.Add(asset);
            await ctx.SaveChangesAsync(Ct);
            ctx.WhitelistedTokens.Add(new WhitelistedToken
            {
                Name = "EUR", Symbol = "EUR", Chain = Chain.Exchange, ContractAddress = null, Decimals = 18, AssetId = asset.Id
            });
            await ctx.SaveChangesAsync(Ct);
        }

        // The stray fiat entry is not shown in the whitelist.
        Assert.Empty(await f.Whitelist.GetAsync(Ct));

        f.Exchange.Holdings.Add(new ExchangePosition("EUR", "EUR", ExchangePosition.PlaceholderId("EUR"), null, null, 100m));
        await f.Scan.ScanAsync(Ct);

        // Exactly one EUR position, from the fiat path, priced through euro-coin.
        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal("EUR", position.Asset!.Symbol);
        Assert.Equal("euro-coin", position.Asset!.CoinGeckoId);
        Assert.Equal(100m, position.Quantity);
    }


    // ---- exchange tokens: whitelisted by ticker, instead of by chain + contract ------------------------------------

    private static WhitelistedTokenInput ExchangeToken(string name, string symbol, string coinGeckoId) =>
        new(name, symbol, Chain.Exchange, null, 18, coinGeckoId);

    [Fact]
    public async Task Scan_Exchange_TickerWhitelistedAsExchange_IsTrackedWithoutAChain()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        // The exchange does not report a chain, so the token is whitelisted by ticker only.
        Assert.True((await f.Whitelist.AddAsync(ExchangeToken("Sei", "SEI", "sei-network"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("SEI", "SEI", ExchangePosition.PlaceholderId("SEI"), null, null, 984.93m));
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Empty(result.Errors);
        var sei = Assert.Single(await f.PositionsAsync());
        Assert.Equal("sei-network", sei.Asset!.CoinGeckoId); // a real asset, so the price sync can price it
        Assert.Equal(PositionSourceType.Exchange, sei.SourceType);
        Assert.Null(sei.Chain); // an exchange holding has no chain
        Assert.Null(sei.WalletAddress);
        Assert.Equal(984.93m, sei.Quantity);
    }

            [Fact]
    public async Task Scan_Exchange_UnwhitelistedHoldingAppearsOnlyOnceWhitelisted()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        // First scan without a whitelist: the SEI holding is not shown at all (and no asset is created for it).
        f.Exchange.Holdings.Add(new ExchangePosition("SEI", "SEI", ExchangePosition.PlaceholderId("SEI"), null, null, 100m));
        await f.Scan.ScanAsync(Ct);

        Assert.Empty(await f.PositionsAsync());
        await using (var ctx = f.Db.CreateContext())
            Assert.False(await ctx.Assets.AnyAsync(a => a.Symbol == "SEI", Ct));

        // Whitelisting the ticker makes it show up on the next scan, once.
        Assert.True((await f.Whitelist.AddAsync(ExchangeToken("Sei", "SEI", "sei-network"), Ct)).Ok);
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(new PositionChanges(1, 0, 0), result.Changes);
        var after = Assert.Single(await f.PositionsAsync());
        Assert.Equal("sei-network", after.Asset!.CoinGeckoId);
        Assert.Equal(100m, after.Quantity);
        Assert.Null(after.Chain);
    }

    [Fact]
    public async Task Scan_Exchange_PlaceholderAssetIsNotAdoptedForAnOnChainTokenWithTheSameTicker()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        // An unpriced holding created the placeholder "exchange-xrp" although a real XRP asset is not tracked yet.
        await using (var ctx = f.Db.CreateContext())
        {
            ctx.Assets.Add(new Asset { Name = "XRP", Symbol = "XRP", CoinGeckoId = ExchangePosition.PlaceholderId("XRP") });
            await ctx.SaveChangesAsync(Ct);
        }

        // Whitelisting the on-chain token with the same ticker must not hijack that placeholder.
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("XRP", "XRP", Chain.XrpLedger, null, 6, "ripple"), Ct)).Ok);

        await using var check = f.Db.CreateContext();
        Assert.Equal("ripple", (await check.Assets.SingleAsync(a => a.CoinGeckoId == "ripple", Ct)).CoinGeckoId);
        Assert.True(await check.Assets.AnyAsync(a => a.CoinGeckoId == ExchangePosition.PlaceholderId("XRP"), Ct));
    }

    [Fact]
    public async Task Scan_Exchange_WhitelistedPerChainTickerBeatsTheSameTickerAsExchangeToken()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(ExchangeToken("Ripple", "XRP", "ripple"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("XRP", "XRP", ExchangePosition.PlaceholderId("XRP"), null, null, 3454.83m));
        await f.Scan.ScanAsync(Ct);

        // One position for the ticker, not an extra price-less one next to it.
        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal("ripple", position.Asset!.CoinGeckoId);
        Assert.Null(position.Chain);
    }

    [Fact]
    public void ExchangeToken_IsWhitelistedByTickerWithoutAnAddress()
    {
        // The whitelist form offers this chain; it has no node and no address format.
        Assert.Equal("Exchange (ticker)", ChainInfo.Label(Chain.Exchange));

        // No contract address is valid (or needed) there, and a made-up one is refused.
        Assert.True(BlockchainAddress.IsValidTokenAddress(Chain.Exchange, null));
        Assert.False(BlockchainAddress.IsValidTokenAddress(Chain.Exchange, "0x1234567890abcdef1234567890abcdef12345678"));
        Assert.Contains("ticker", BlockchainAddress.TokenAddressHint(Chain.Exchange));
    }

    [Fact]
    public async Task Whitelist_ExchangeToken_TakesTheTickerAndIgnoresAnAddress()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        // No RPC node is needed for an exchange, and an exchange token is never matched by address.
        var result = await f.Whitelist.AddAsync(ExchangeToken("Sei", "SEI", "sei-network") with
        {
            ContractAddress = "0x1234567890abcdef1234567890abcdef12345678"
        }, Ct);
        Assert.True(result.Ok, result.Error);

        var token = Assert.Single(await f.Whitelist.GetAsync(Ct));
        Assert.Equal(Chain.Exchange, token.Chain);
        Assert.Null(token.ContractAddress);

        // Same ticker twice on "Exchange" would be the same position, so it is refused.
        Assert.Contains("already whitelisted", (await f.Whitelist.AddAsync(ExchangeToken("Sei v2", "sei", "sei-v2"), Ct)).Error);

                // An on-chain token with the same ticker is a different position and stays allowed.
        Assert.True((await f.Whitelist.AddAsync(
            new WhitelistedTokenInput("Sei on Cronos", "SEI", Chain.Cronos, "0x0000000000000000000000000000000000000abc", 18, "sei-network-cronos"), Ct)).Ok);
    }

    [Fact]
    public async Task Whitelist_RejectsFiatAsAnExchangeToken()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        // EUR is priced and shown by the exchange scan itself; it is not something the user whitelists.
        var result = await f.Whitelist.AddAsync(ExchangeToken("Euro", "EUR", "euro-coin"), Ct);
        Assert.False(result.Ok);
        Assert.Contains("fiat", result.Error);

        Assert.Empty(await f.Whitelist.GetAsync(Ct));
    }

    [Fact]
    public async Task Scan_Exchange_WithFailedRead_ReportsTheError_AndKeepsTheStoredPositions()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);

        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 5m));
        await f.Scan.ScanAsync(Ct);

        f.Exchange.Failure = new ExchangeBalanceException("Bitvavo rejected the API key or signature (HTTP 401).");
        var result = await f.Scan.ScanAsync(Ct);

        Assert.Contains(result.Errors, e => e.Contains("HTTP 401"));
        Assert.Equal(5m, Assert.Single(await f.PositionsAsync()).Quantity); // a failed read is not a zero balance

        await using var check = f.Db.CreateContext();
        var wallet = await check.Wallets.SingleAsync(Ct);
        Assert.Contains("401", wallet.LastScanError);
        Assert.NotNull(wallet.LastScannedAtUtc); // the earlier success is still recorded as the last complete scan
    }

    [Fact]
    public async Task Scan_Exchange_WithUnsupportedSource_IsSkipped_NotFailed()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        // A wallet saved before an exchange was removed, or a hand-edited source, must not break the whole scan.
        await using (var ctx = f.Db.CreateContext())
        {
            ctx.Wallets.Add(new Wallet { Address = "", Name = "Old exchange", Type = WalletType.Cex, Source = "MtGox" });
            await ctx.SaveChangesAsync(Ct);
        }

        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(0, result.WalletsScanned);
        Assert.Equal(1, result.WalletsSkipped);
        Assert.Contains(result.Errors, e => e.Contains("MtGox"));
        Assert.Equal(0, f.Exchange.Calls);
    }

    [Fact]
    public async Task ScanWallet_OnExchange_ReadsOnlyThatWallet()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);
        f.Exchange.Holdings.Add(new ExchangePosition("CRO", "CRO", "exchange-cro", null, null, 7m));

        var wallet = (await f.Wallets.GetWalletsAsync(Ct)).Single();
        var result = await f.Scan.ScanWalletAsync(wallet.Id, Ct);

        Assert.Equal(1, result.WalletsScanned);
        Assert.Empty(result.Errors);
        Assert.Equal(1, f.Exchange.Calls);
        Assert.Equal(7m, Assert.Single(await f.PositionsAsync()).Quantity);
    }

    [Fact]
    public async Task Scan_RemovesPositions_WhenTokenIsNoLongerWhitelisted()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, UsdcCronos)] = 5m;
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);

        var usdcToken = (await f.Repository.GetWhitelistAsync(Ct)).Single(t => t.Symbol == "USDC" && t.Chain == Chain.Cronos);
        await f.Whitelist.DeleteAsync(usdcToken.Id, Ct);

        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal("CRO", position.Asset!.Symbol);

        f.Reader.Calls.Clear();
        await f.Scan.ScanAsync(Ct);
        Assert.DoesNotContain(f.Reader.Calls, c => c.Contract == UsdcCronos);
    }

    [Fact]
    public async Task Scan_UsesSameAssetForUsdcOnBothChains_AndSumsQuantities()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, UsdcCronos)] = 10m;
        f.Reader.Balances[(AddressA, Chain.BnbSmartChain, "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d")] = 2.5m;
        await f.Scan.ScanAsync(Ct);

        var positions = await f.PositionsAsync();
        Assert.Equal(2, positions.Count);
        Assert.Single(positions.Select(p => p.AssetId).Distinct());

        var overview = await new PortfolioService(new PortfolioRepository(f.Db.CreateFactory()), f.Clock).GetPortfolioOverviewAsync(Ct);
        Assert.Equal(12.5m, overview.Assets.Single(a => a.Symbol == "USDC").Quantity);
    }

    [Fact]
    public async Task Scan_WhileAnotherScanRuns_ReturnsBusy()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Gate = new TaskCompletionSource();

        var first = f.Scan.ScanAsync(Ct);
        var second = await f.Scan.ScanAsync(Ct);

        Assert.True(second.AlreadyRunning);
        f.Reader.Gate.SetResult();
        Assert.False((await first).AlreadyRunning);
    }

    [Fact]
    public async Task Scan_WithoutWallets_IsHarmless()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(0, result.WalletsScanned);
        Assert.Empty(result.Errors);
    }

    // ---- wallet management --------------------------------------------------------------------------------------

    [Fact]
    public async Task Wallet_Validation_RejectsBadInput()
    {
        await using var f = await Fixture.CreateAsync();

        Assert.False((await f.Wallets.AddAsync(new WalletInput(AddressA, "", WalletType.Hot, "x"), Ct)).Ok);
        Assert.False((await f.Wallets.AddAsync(new WalletInput(AddressA, "x", WalletType.Hot, " "), Ct)).Ok);
        Assert.False((await f.Wallets.AddAsync(new WalletInput("0x123", "x", WalletType.Hot, "x"), Ct)).Ok);
        Assert.False((await f.Wallets.AddAsync(new WalletInput("not-an-address", "x", WalletType.Cold, "x"), Ct)).Ok);

        var duplicate = await f.Wallets.AddAsync(new WalletInput(AddressA.ToLowerInvariant(), "Other", WalletType.Cold, "Ledger"), Ct);
        Assert.Contains("already exists", duplicate.Error);

        Assert.Equal(2, (await f.Wallets.GetWalletsAsync(Ct)).Count);
    }

    [Fact]
    public async Task Wallet_AddEditDelete_Works_AndTrimsInput()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput($"  {AddressA} ", " Ledger Nano ", WalletType.Cold, " Ledger "), Ct)).Ok);

        var wallet = Assert.Single(await f.Wallets.GetWalletsAsync(Ct));
        Assert.Equal((AddressA, "Ledger Nano", WalletType.Cold, "Ledger"), (wallet.Address, wallet.Name, wallet.Type, wallet.Source));

        Assert.True((await f.Wallets.UpdateAsync(wallet.Id, new WalletInput(AddressA, "Ledger Main", WalletType.Cold, "Ledger"), Ct)).Ok);
        Assert.Equal("Ledger Main", Assert.Single(await f.Wallets.GetWalletsAsync(Ct)).Name);

        await f.Wallets.DeleteAsync(wallet.Id, Ct);
        Assert.Empty(await f.Wallets.GetWalletsAsync(Ct));
    }

    [Fact]
    public async Task Wallet_Rename_UpdatesScannedPositionNames_ChangeOfAddress_RemovesThem_DeleteCascades()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);
        var wallet = (await f.Wallets.GetWalletsAsync(Ct)).Single(w => w.Address == AddressA);

        await f.Wallets.UpdateAsync(wallet.Id, new WalletInput(AddressA, "Renamed", WalletType.Hot, "Metamask"), Ct);
        Assert.Equal("Renamed", Assert.Single(await f.PositionsAsync()).WalletOrExchangeName);

        await f.Wallets.UpdateAsync(wallet.Id, new WalletInput("0x0000000000000000000000000000000000000009", "Renamed", WalletType.Hot, "Metamask"), Ct);
        Assert.Empty(await f.PositionsAsync());

        f.Reader.Balances[("0x0000000000000000000000000000000000000009", Chain.Cronos, null)] = 3m;
        await f.Scan.ScanAsync(Ct);
        Assert.Single(await f.PositionsAsync());

                        await f.Wallets.DeleteAsync(wallet.Id, Ct);
        Assert.Empty(await f.PositionsAsync());
    }

            [Fact]
    public async Task Wallet_TypeIsFixedAtCreation_AndAnEditCannotTurnAWalletIntoACex()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.Balances[(AddressA, Chain.Cronos, null)] = 100m;
        await f.Scan.ScanAsync(Ct);
        var wallet = (await f.Wallets.GetWalletsAsync(Ct)).Single(w => w.Address == AddressA);
        Assert.Equal(WalletType.Hot, wallet.Type);

        // An edit that asks for CEX is accepted but the stored type (and therefore the way it is read) stays hot.
        Assert.True((await f.Wallets.UpdateAsync(wallet.Id, new WalletInput(AddressA, "Renamed", WalletType.Cex, "Bitvavo"), Ct)).Ok);

        var updated = (await f.Wallets.GetWalletsAsync(Ct)).Single(w => w.Id == wallet.Id);
        Assert.Equal(WalletType.Hot, updated.Type);
        Assert.Equal("Renamed", updated.Name);

        // The wallet is still read on-chain, and the scanned position survives the rename.
        var position = Assert.Single(await f.PositionsAsync());
        Assert.Equal(100m, position.Quantity);
        Assert.Equal("Renamed", position.WalletOrExchangeName);
    }

    [Fact]
    public async Task Wallet_UpdatingAMissingWallet_FailsInsteadOfThrowing()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        var result = await f.Wallets.UpdateAsync(999, new WalletInput(AddressA, "Ghost", WalletType.Hot, "Metamask"), Ct);

        Assert.False(result.Ok);
        Assert.Contains("no longer exists", result.Error);
    }

    // ---- whitelist management -----------------------------------------------------------------------------------

    [Fact]
    public async Task Whitelist_Validation_RejectsBadInput()
    {
        await using var f = await Fixture.CreateAsync();
        var ok = new WhitelistedTokenInput("Token", "TKN", Chain.Cronos, "0x0000000000000000000000000000000000000abc", 18, "token-x");

        Assert.False((await f.Whitelist.AddAsync(ok with { Name = " " }, Ct)).Ok);
        Assert.False((await f.Whitelist.AddAsync(ok with { Symbol = "" }, Ct)).Ok);
        Assert.False((await f.Whitelist.AddAsync(ok with { CoinGeckoId = "" }, Ct)).Ok);
        Assert.False((await f.Whitelist.AddAsync(ok with { ContractAddress = "0x12" }, Ct)).Ok);
        Assert.False((await f.Whitelist.AddAsync(ok with { Decimals = 37 }, Ct)).Ok);
        Assert.Contains("not supported", (await f.Whitelist.AddAsync(ok with { Chain = Chain.Bitcoin }, Ct)).Error);

        // Same contract again (different casing) and a second native coin on the same chain.
        Assert.Contains("already", (await f.Whitelist.AddAsync(ok with { ContractAddress = UsdcCronos.ToLowerInvariant() }, Ct)).Error);
        Assert.Contains("already", (await f.Whitelist.AddAsync(ok with { ContractAddress = null }, Ct)).Error);
        // Same asset twice on one chain would collide on the position key.
        Assert.Contains("already whitelisted", (await f.Whitelist.AddAsync(ok with { CoinGeckoId = "usd-coin" }, Ct)).Error);

        Assert.Equal(5, (await f.Whitelist.GetAsync(Ct)).Count);
    }

    [Fact]
    public async Task Whitelist_AddCreatesAsset_OrReusesExisting_AndEditWorks()
    {
        await using var f = await Fixture.CreateAsync();
        await using var ctx = f.Db.CreateContext();
        var assetsBefore = await ctx.Assets.CountAsync(Ct);

        // New identifier: a new asset is created.
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "PancakeSwap", "CAKE", Chain.BnbSmartChain, "0x0E09FaBB73Bd3Ade0a17ECC321fD13a19e81cE82", 18, " PancakeSwap-Token "), Ct)).Ok);
        Assert.Equal(assetsBefore + 1, await ctx.Assets.CountAsync(Ct));
        Assert.Equal(1, await ctx.Assets.CountAsync(a => a.CoinGeckoId == "pancakeswap-token", Ct)); // trimmed + lower-cased

        // Existing identifier on another chain: the asset is reused.
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "XRP", "XRP", Chain.Cronos, "0x0000000000000000000000000000000000000def", 6, "ripple"), Ct)).Ok);
        Assert.Equal(assetsBefore + 1, await ctx.Assets.CountAsync(Ct));

        var cake = (await f.Whitelist.GetAsync(Ct)).Single(t => t.Symbol == "CAKE");
        Assert.True((await f.Whitelist.UpdateAsync(cake.Id, new WhitelistedTokenInput(
            "Cake", "CAKE2", Chain.BnbSmartChain, cake.ContractAddress, 8, "pancakeswap-token"), Ct)).Ok);

        var edited = (await f.Whitelist.GetAsync(Ct)).Single(t => t.Id == cake.Id);
        Assert.Equal(("Cake", "CAKE2", 8), (edited.Name, edited.Symbol, edited.Decimals));
        Assert.Equal(7, (await f.Whitelist.GetAsync(Ct)).Count);
    }

    // ---- non-EVM chains -----------------------------------------------------------------------------------------

    private const string XrpAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    private const string SolAccount = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";

    [Fact]
    public async Task Scan_ReadsEachWalletOnlyOnChainsOfItsOwnFamily()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True((await f.Wallets.AddAsync(new WalletInput(XrpAccount, "XRP wallet", WalletType.Cold, "Ledger"), Ct)).Ok);
        Assert.True((await f.Wallets.AddAsync(new WalletInput(SolAccount, "Sol wallet", WalletType.Hot, "Phantom"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("XRP", "XRP", Chain.XrpLedger, null, 6, "ripple"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Solana", "SOL", Chain.Solana, null, 9, "solana"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "USD Coin", "USDC", Chain.Solana, "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", 6, "usd-coin"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Ether", "ETH", Chain.Scroll, null, 18, "ethereum"), Ct)).Ok);

        f.Reader.Balances[(XrpAccount, Chain.XrpLedger, null)] = 420m;
        f.Reader.Balances[(SolAccount, Chain.Solana, null)] = 3m;
        f.Reader.Balances[(AddressA, Chain.Scroll, null)] = 0.5m;

        var result = await f.Scan.ScanAsync(Ct);

        Assert.Empty(result.Errors);
        Assert.All(f.Reader.Calls.Where(c => c.Wallet == XrpAccount), c => Assert.Equal(Chain.XrpLedger, c.Chain));
        Assert.All(f.Reader.Calls.Where(c => c.Wallet == SolAccount), c => Assert.Equal(Chain.Solana, c.Chain));
        Assert.All(f.Reader.Calls.Where(c => c.Wallet == AddressA), c => Assert.NotEqual(Chain.Solana, c.Chain));
        Assert.DoesNotContain(f.Reader.Calls, c => c.Chain == Chain.XrpLedger && c.Wallet != XrpAccount);

        var positions = await f.PositionsAsync();
        Assert.Contains(positions, p => p is { Chain: Chain.XrpLedger, Quantity: 420m } && p.WalletAddress == XrpAccount);
        Assert.Contains(positions, p => p is { Chain: Chain.Solana, Quantity: 3m } && p.WalletAddress == SolAccount);
        Assert.Contains(positions, p => p is { Chain: Chain.Scroll, Quantity: 0.5m } && p.WalletAddress == AddressA);
    }

    [Fact]
    public async Task Wallets_AcceptAllSupportedAddressFormats_AndRejectGarbage()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        Assert.True((await f.Wallets.AddAsync(new WalletInput(XrpAccount, "X", WalletType.Cold, "Ledger"), Ct)).Ok);
        Assert.True((await f.Wallets.AddAsync(new WalletInput(SolAccount, "S", WalletType.Hot, "Phantom"), Ct)).Ok);
        Assert.False((await f.Wallets.AddAsync(new WalletInput("rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTi", "Bad", WalletType.Hot, "x"), Ct)).Ok);
        Assert.False((await f.Wallets.AddAsync(new WalletInput("hello", "Bad", WalletType.Hot, "x"), Ct)).Ok);
    }

    [Fact]
    public async Task Whitelist_ValidatesTokenAddressPerChain()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);

        var evmOnSolana = await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "USD Coin", "USDC", Chain.Solana, "0xc21223249CA28397B4B6541dfFaEcC539BfF0c59", 6, "usd-coin"), Ct);
        Assert.Contains("mint", evmOnSolana.Error);

        var badIssuer = await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "USD", "USD", Chain.XrpLedger, "USD.rnotvalid", 15, "usd-coin"), Ct);
        Assert.Contains("CURRENCY.issuer", badIssuer.Error);

        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput(
            "Bitstamp USD", "USD", Chain.XrpLedger, "USD.rvYAfWj5gh67oV6fW32ZzP3Aw4Eubs59B", 15, "usd-coin"), Ct)).Ok);
    }
}