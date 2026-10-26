using CryptoPortfolioManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

/// <summary>
/// The wallets and token whitelist the application starts with. Inserted only when both lists are empty (first start),
/// so wallets or tokens the user removed later are not brought back. Contains no balances and no prices.
/// Token contracts were checked against the public nodes (symbol and decimals).
/// </summary>
public static class DefaultReferenceData
{
    public static async Task<bool> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Wallets.AnyAsync(ct) || await db.WhitelistedTokens.AnyAsync(ct)) return false;

        db.Wallets.AddRange(
            new Wallet { Address = "0x59711D16ff7F32EAe184BD1B892C7e6d6C058bFc", Name = "Cronos Main", Type = WalletType.Hot, Source = "Metamask" },
            new Wallet { Address = "0x1ED7E061015b62a1D57266cFd2ef3bE8F96FF8e3", Name = "Thor Vapor", Type = WalletType.Hot, Source = "Metamask" });

        var cro = await AssetAsync(db, "Cronos", "CRO", "crypto-com-chain", ct);
        var usdc = await AssetAsync(db, "USD Coin", "USDC", "usd-coin", ct);
        var xrp = await AssetAsync(db, "XRP", "XRP", "ripple", ct);
        var bnb = await AssetAsync(db, "BNB", "BNB", "binancecoin", ct);

        db.WhitelistedTokens.AddRange(
            new WhitelistedToken { Name = "Cronos", Symbol = "CRO", Chain = Chain.Cronos, Decimals = 18, Asset = cro },
            new WhitelistedToken
            {
                Name = "USD Coin", Symbol = "USDC", Chain = Chain.Cronos, Decimals = 6, Asset = usdc,
                ContractAddress = "0xc21223249CA28397B4B6541dfFaEcC539BfF0c59"
            },
            new WhitelistedToken
            {
                Name = "XRP", Symbol = "XRP", Chain = Chain.BnbSmartChain, Decimals = 18, Asset = xrp,
                ContractAddress = "0x1D2F0da169ceB9fC7B3144628dB156f3F6c60dBE"
            },
            new WhitelistedToken
            {
                Name = "USD Coin", Symbol = "USDC", Chain = Chain.BnbSmartChain, Decimals = 18, Asset = usdc,
                ContractAddress = "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d"
            },
            new WhitelistedToken { Name = "BNB", Symbol = "BNB", Chain = Chain.BnbSmartChain, Decimals = 18, Asset = bnb });

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<Asset> AssetAsync(AppDbContext db, string name, string symbol, string coinGeckoId, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.CoinGeckoId == coinGeckoId, ct);
        if (asset is not null) return asset;

        asset = new Asset { Name = name, Symbol = symbol, CoinGeckoId = coinGeckoId };
        db.Assets.Add(asset);
        return asset;
    }
}
