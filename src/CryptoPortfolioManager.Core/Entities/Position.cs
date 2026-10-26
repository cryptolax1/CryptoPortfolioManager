namespace CryptoPortfolioManager.Core.Entities;

/// <summary>Where a position's quantity comes from. Lets a future sync job know what it may overwrite.</summary>
public enum PositionSourceType
{
    /// <summary>Entered by hand; never touched by automatic syncs.</summary>
    Manual,
    /// <summary>Read from a blockchain address.</summary>
    OnChainWallet,
    /// <summary>Read from an exchange account.</summary>
    Exchange
}

public class Position
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>Name of the wallet or exchange (e.g. "Ledger", "Kraken").</summary>
    public required string WalletOrExchangeName { get; set; }

    public PositionSourceType SourceType { get; set; } = PositionSourceType.Manual;

    public Chain? Chain { get; set; }

    /// <summary>Public address; together with <see cref="Chain"/> it is what a later wallet reader needs.</summary>
    public string? WalletAddress { get; set; }

    /// <summary>Amount in whole tokens (already adjusted for token decimals).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Set when the wallet scan owns this position; null for manual positions, which a scan never touches.</summary>
    public int? WalletId { get; set; }
    public Wallet? Wallet { get; set; }

    public string? Notes { get; set; }

    /// <summary>When the quantity was last read automatically; null for manual positions.</summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
