using System.Globalization;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;

namespace CryptoPortfolioManager.Web.Components.Portfolio;

/// <summary>Display formatting only; no business calculations.</summary>
public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("nl-NL");

    public const string Empty = "–";

    /// <summary>UTC timestamp shown in the local time of this PC.</summary>
    public static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", Culture);

    public static string Usd(decimal? value) => value is null
        ? Empty
        : "$ " + value.Value.ToString(Math.Abs(value.Value) < 1m ? "#,##0.00######" : "#,##0.00", Culture);

    public static string Quantity(decimal value) => value.ToString("#,##0.########", Culture);

    public static string Percent(decimal? fraction) => fraction is null
        ? Empty
        : (fraction.Value * 100m).ToString("+0.00;-0.00;0.00", Culture) + "%";

    public static string ShortAddress(string? address) => string.IsNullOrEmpty(address)
        ? Empty
        : address.Length <= 14 ? address : $"{address[..6]}…{address[^4..]}";

    public static string ChainName(Chain? chain) => chain?.ToString() ?? Empty;

    public static string ChainLabel(Chain chain) => ChainInfo.Label(chain);

        public static string WalletTypeLabel(WalletType type) => WalletTypeNames.Label(type);

    /// <summary>
    /// Whether the re-scan can read this wallet: an on-chain address for a wallet, and a supported exchange (whose API
    /// then supplies the balances) for an exchange account.
    /// </summary>
    public static bool IsScannable(Wallet wallet) => wallet.Type == WalletType.Cex
        ? ExchangeCatalog.Match(wallet.Source) is not null
        : BlockchainAddress.DetectFamily(wallet.Address) is not null;

    /// <summary>What a scan of this wallet will read, or why it cannot: shown next to the re-scan button.</summary>
    public static string ScanSource(Wallet wallet) => wallet.Type == WalletType.Cex
        ? $"balances come from the {ExchangeCatalog.Match(wallet.Source) ?? wallet.Source} API"
        : "balances are read on-chain";

    /// <summary>CSS modifier for delta coloring.</summary>
    public static string DeltaClass(decimal? fraction) => fraction switch
    {
        null => "delta-none",
        > 0m => "delta-pos",
        < 0m => "delta-neg",
        _ => "delta-flat"
    };
}
