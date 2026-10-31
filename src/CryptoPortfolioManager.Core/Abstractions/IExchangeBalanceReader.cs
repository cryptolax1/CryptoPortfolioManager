using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Abstractions;

/// <summary>
/// One holding of a centralized exchange account, ready to be stored as a position.
/// <paramref name="Symbol"/> is the exchange's ticker (e.g. "BTC", "COMP") and is what decides whether the holding is
/// tracked: it is matched case-insensitively with the symbols on the token whitelist.
/// </summary>
public sealed record ExchangePosition(
    string Symbol,
    string AssetName,
    string CoinGeckoId,
    Chain? Chain,
    string? ContractAddress,
    decimal Quantity)
{
    /// <summary>
    /// The CoinGecko id of an asset that only an exchange holding knows about (e.g. <c>exchange-sei</c>): a ticker,
    /// not a CoinGecko id, so the asset has no price. Whitelisting the ticker gives the asset its real id instead.
    /// </summary>
    public const string PlaceholderIdPrefix = "exchange-";

    public static string PlaceholderId(string symbol) => PlaceholderIdPrefix + symbol.Trim().ToLowerInvariant();

    public static bool IsPlaceholderId(string? coinGeckoId) =>
        coinGeckoId is not null && coinGeckoId.StartsWith(PlaceholderIdPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fiat currencies an exchange reports next to the crypto balances. They are cash, not tokens, but a user still wants
/// to see them under the exchange wallet. The balance endpoints of Bitvavo and Crypto.com list them (e.g. an EUR
/// deposit) with the same symbol/available shape as a coin. Unlike other un-whitelisted holdings, fiat cash is always
/// shown on an exchange wallet: it is mapped to a real CoinGecko asset (EUR to "euro-coin") purely so it gets a price,
/// while the wallet keeps showing it under the exchange's own ticker (EUR). Only currencies with a known CoinGecko id
/// are listed; an unmapped fiat ticker is treated like any other un-whitelisted coin and is not shown.
/// </summary>
public static class FiatCurrencies
{
    /// <summary>
    /// Fiat tickers mapped to the CoinGecko id of their closest priced coin. Compared case-insensitively, so "eur",
    /// "EUR" and " Eur " match. EUR is priced through EURC, a euro-backed stablecoin, because CoinGecko has no price for
    /// the euro itself; the value tracks 1:1 closely enough to count in the portfolio total.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CoinGeckoIds { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EUR"] = "euro-coin",
        };

    /// <summary>The CoinGecko id that prices a fiat ticker, or null when the fiat currency has no mapped coin.</summary>
    public static string? CoinGeckoIdFor(string? symbol) =>
        symbol is not null && CoinGeckoIds.TryGetValue(symbol.Trim(), out var id) ? id : null;

    public static bool IsFiat(string? symbol) => CoinGeckoIdFor(symbol) is not null;

    /// <summary>
    /// Whether a CoinGecko id is one a fiat currency is priced through (e.g. "euro-coin"). Used to recognise the asset a
    /// fiat holding created, so it is never mistaken for a token the user whitelisted.
    /// </summary>
    public static bool IsFiatAssetId(string? coinGeckoId) =>
        coinGeckoId is not null && CoinGeckoIds.Values.Any(id => string.Equals(id, coinGeckoId.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the balances of an exchange account through the exchange's own private, key-authenticated REST API.
/// The wallet scan picks the implementation that matches the wallet's <see cref="Wallet.Source"/>.
/// </summary>
public interface IExchangeBalanceReader
{
    /// <summary>The exchange name, identical to the value of <see cref="ExchangeCatalog"/> and the wallet source.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the API credentials for this exchange are present in configuration. Only a hint for the user interface;
    /// when they are missing <see cref="GetBalancesAsync"/> fails with a message that says how to set them.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>All non-zero holdings of the account.</summary>
    /// <exception cref="ExchangeBalanceException">The balances could not be read reliably.</exception>
    Task<IReadOnlyList<ExchangePosition>> GetBalancesAsync(CancellationToken ct = default);
}

/// <summary>Exchange balances could not be read (missing credentials, rejected key, network error, bad response).</summary>
public class ExchangeBalanceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The exchanges the wallet scan can read, and how their names are matched with a wallet's source.</summary>
public static class ExchangeCatalog
{
    public const string Bitvavo = "Bitvavo";
    public const string CryptoCom = "Crypto.com";

    /// <summary>Names offered in the user interface and accepted as the source of a CEX wallet.</summary>
    public static IReadOnlyList<string> All { get; } = [Bitvavo, CryptoCom];

    /// <summary>
    /// The catalogue name for the free-text source of a wallet, or null when the source is not a supported exchange.
    /// Ignores case, spaces, dots and hyphens, so "crypto com", "Crypto.com" and "CryptoCom" are the same exchange.
    /// </summary>
    public static string? Match(string? source)
    {
        var normalized = Normalize(source);
        if (normalized.Length == 0) return null;

        return All.FirstOrDefault(name => Normalize(name) == normalized);
    }

    /// <summary>
    /// The user-secrets keys of an exchange's credentials, e.g. <c>Exchange:Bitvavo:ApiKey</c>. Neither catalogue name
    /// contains a character that needs escaping in a configuration key.
    /// </summary>
    public static (string ApiKey, string ApiSecret) CredentialKeys(string exchange) =>
        ($"Exchange:{exchange}:ApiKey", $"Exchange:{exchange}:ApiSecret");

    private static string Normalize(string? value) =>
        value is null ? "" : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
