namespace CryptoPortfolioManager.Core.Entities;

public enum PositionChangeType
{
    Added,
    Updated,
    Removed
}

/// <summary>
/// Append-only history of position changes. <see cref="Position"/> holds only the current state; this log lets you see
/// which assets were held at an earlier moment. Asset and wallet details are copied so the log survives later deletes.
/// </summary>
public class PositionChangeLog
{
    public int Id { get; set; }

    public DateTime OccurredAtUtc { get; set; }
    public PositionChangeType ChangeType { get; set; }

    /// <summary>Why the change happened, e.g. "Wallet scan", "Not found by scan", "Wallet deleted".</summary>
    public required string Reason { get; set; }

    /// <summary>Not a foreign key on purpose: the log must outlive the asset.</summary>
    public int AssetId { get; set; }
    public required string AssetSymbol { get; set; }
    public required string AssetName { get; set; }

    public required string WalletOrExchangeName { get; set; }
    public Chain? Chain { get; set; }
    public string? WalletAddress { get; set; }

    /// <summary>Null when the position was added.</summary>
    public decimal? PreviousQuantity { get; set; }

    /// <summary>Null when the position was removed.</summary>
    public decimal? NewQuantity { get; set; }
}
