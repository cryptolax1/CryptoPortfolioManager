using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Models;

public enum DeltaPeriod
{
    Ytd,
    ThreeMonths,
    OneMonth,
    OneWeek,
    OneDay
}

/// <summary>One position (wallet/exchange holding) within an asset row.</summary>
public record PositionLine(
    int PositionId,
    string WalletOrExchangeName,
    Chain? Chain,
    string? WalletAddress,
    decimal Quantity,
    decimal? ValueUsd)
{
    /// <summary>
    /// Where the quantity comes from. An exchange position normally has no chain; this tells it apart from an empty
    /// chain in the user interface.
    /// </summary>
    public PositionSourceType SourceType { get; init; } = PositionSourceType.Manual;
}

/// <summary>One main row of the overview: an asset with totals aggregated from its positions.</summary>
public record AssetOverview(
    int AssetId,
    string Name,
    string Symbol,
    string CoinGeckoId,
    decimal Quantity,
    decimal? PriceUsd,
    decimal? ValueUsd,
    IReadOnlyDictionary<DeltaPeriod, decimal?> Deltas,
    IReadOnlyList<PositionLine> Positions)
{
    /// <summary>Price change as a fraction (0.05 = +5%); null when there is not enough price history.</summary>
    public decimal? DeltaFor(DeltaPeriod period) => Deltas.GetValueOrDefault(period);
}

public record PortfolioOverview(IReadOnlyList<AssetOverview> Assets)
{
    /// <summary>Sum of all assets that have a price; assets without price are not included.</summary>
    public decimal TotalValueUsd => Assets.Sum(a => a.ValueUsd ?? 0m);

    public int AssetsWithoutPrice => Assets.Count(a => a.PriceUsd is null);
}
