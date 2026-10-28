namespace CryptoPortfolioManager.Core.Entities;

public enum WalletType
{
    /// <summary>Offline / hardware wallet.</summary>
    Cold,
    /// <summary>Software wallet connected to the internet (e.g. MetaMask).</summary>
    Hot,
    /// <summary>
    /// Centralized exchange account. A CEX holds no on-chain key: its balances live in the exchange's database and are
    /// read through the exchange's own API. The API is chosen by <see cref="Source"/> (the exchange's name), so the
    /// address stays empty.
    /// </summary>
        Cex
}

public static class WalletTypeNames
{
    /// <summary>User-facing name of a wallet type; "Exchange" rather than "Cex".</summary>
    public static string Label(WalletType type) => type switch
    {
        WalletType.Cex => "Exchange",
        _ => type.ToString()
    };
}

/// <summary>A wallet whose holdings are read by the wallet scan.</summary>
public class Wallet
{
    public int Id { get; set; }

    /// <summary>
    /// Public address of an on-chain wallet. Empty for a CEX, whose balances are read from the exchange's API instead.
    /// For EVM wallets the same address is valid on every EVM chain.
    /// </summary>
    public required string Address { get; set; }

    public required string Name { get; set; }

    public WalletType Type { get; set; } = WalletType.Hot;

        /// <summary>
    /// Free text: the software, device or exchange (e.g. "Metamask", "Ledger"). For a CEX this must be the exchange's
    /// name ("Bitvavo" or "Crypto.com"), because that name selects the API used to read its balances.
    /// </summary>
    public required string Source { get; set; }

    /// <summary>Last scan in which every whitelisted token was read successfully.</summary>
    public DateTime? LastScannedAtUtc { get; set; }

    /// <summary>Errors of the most recent scan; null when it succeeded completely.</summary>
    public string? LastScanError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Position> Positions { get; set; } = [];
}

