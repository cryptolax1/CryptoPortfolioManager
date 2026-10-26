namespace CryptoPortfolioManager.Core.Entities;

public enum WalletType
{
    /// <summary>Offline / hardware wallet.</summary>
    Cold,
    /// <summary>Software wallet connected to the internet (e.g. MetaMask).</summary>
    Hot,
    /// <summary>Centralized exchange account. Cannot be read on-chain, so it is skipped by the wallet scan.</summary>
    Cex
}

/// <summary>A wallet whose holdings are read by the wallet scan.</summary>
public class Wallet
{
    public int Id { get; set; }

    /// <summary>Public address. For EVM wallets the same address is valid on every EVM chain.</summary>
    public required string Address { get; set; }

    public required string Name { get; set; }

    public WalletType Type { get; set; } = WalletType.Hot;

    /// <summary>Free text: the software, device or exchange (e.g. "Metamask", "Ledger").</summary>
    public required string Source { get; set; }

    /// <summary>Last scan in which every whitelisted token was read successfully.</summary>
    public DateTime? LastScannedAtUtc { get; set; }

    /// <summary>Errors of the most recent scan; null when it succeeded completely.</summary>
    public string? LastScanError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Position> Positions { get; set; } = [];
}
