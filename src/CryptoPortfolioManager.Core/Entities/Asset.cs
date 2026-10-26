namespace CryptoPortfolioManager.Core.Entities;

public class Asset
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Ticker (e.g. BTC). Not unique: different tokens can share a symbol.</summary>
    public required string Symbol { get; set; }

    /// <summary>CoinGecko coin id (e.g. "bitcoin"). Unique.</summary>
    public required string CoinGeckoId { get; set; }

    /// <summary>Chain of the token contract; null for native coins (BTC, ETH, ...).</summary>
    public Chain? ContractChain { get; set; }

    public string? ContractAddress { get; set; }

    /// <summary>Token decimals as defined by the contract; needed to convert raw on-chain balances.</summary>
    public int? TokenDecimals { get; set; }

    public List<Position> Positions { get; set; } = [];

    public List<PriceSnapshot> PriceSnapshots { get; set; } = [];
}
