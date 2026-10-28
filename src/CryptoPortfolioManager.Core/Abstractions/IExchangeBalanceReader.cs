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
    decimal Quantity);

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
