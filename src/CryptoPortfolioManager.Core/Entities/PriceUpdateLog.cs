namespace CryptoPortfolioManager.Core.Entities;

public enum PriceUpdateStatus
{
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed
}

public class PriceUpdateLog
{
    public int Id { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>Null while the update is still running.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    public PriceUpdateStatus Status { get; set; } = PriceUpdateStatus.Running;

    public string? ErrorMessage { get; set; }
}
