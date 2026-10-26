namespace CryptoPortfolioManager.Core.Entities;

public class PriceSnapshot
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset? Asset { get; set; }

    public decimal PriceUsd { get; set; }

    public DateTime TimestampUtc { get; set; }

    /// <summary>Name of the price provider (e.g. "CoinGecko", "Manual").</summary>
    public required string Source { get; set; }
}
