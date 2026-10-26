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

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required WalletRepository Repository { get; init; }
        public required FakeReader Reader { get; init; }
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
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
            return new Fixture
            {
                Db = db, Repository = repository, Reader = reader, Clock = clock,
                Scan = new WalletScanService(repository, reader, new WalletScanLock(), clock)
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
    public async Task Scan_SkipsCexWallets()
    {
        await using var f = await Fixture.CreateAsync(defaults: false);
        Assert.True((await f.Wallets.AddAsync(new WalletInput("my-bitvavo-account", "Bitvavo", WalletType.Cex, "Bitvavo"), Ct)).Ok);
        Assert.True((await f.Whitelist.AddAsync(new WhitelistedTokenInput("Cronos", "CRO", Chain.Cronos, null, 18, "crypto-com-chain"), Ct)).Ok);

        var result = await f.Scan.ScanAsync(Ct);

        Assert.Equal(0, result.WalletsScanned);
        Assert.Equal(1, result.WalletsSkipped);
        Assert.Empty(f.Reader.Calls);
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