using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public enum AssetSortColumn
{
    Name,
    Symbol,
    Quantity,
    Price,
    Value,
    DeltaYtd,
    Delta3M,
    Delta1M,
    Delta1W,
    Delta1D
}

public static class AssetSorter
{
    /// <summary>Sorts rows; rows without a value (no price/history) always end up last.</summary>
    public static IReadOnlyList<AssetOverview> Sort(
        IEnumerable<AssetOverview> assets, AssetSortColumn column, bool descending)
    {
        var list = assets.ToList();

        switch (column)
        {
            case AssetSortColumn.Name:
                return Order(list, a => a.Name, descending, StringComparer.CurrentCultureIgnoreCase);
            case AssetSortColumn.Symbol:
                return Order(list, a => a.Symbol, descending, StringComparer.CurrentCultureIgnoreCase);
            case AssetSortColumn.Quantity:
                return Order(list, a => (decimal?)a.Quantity, descending);
            case AssetSortColumn.Price:
                return Order(list, a => a.PriceUsd, descending);
            case AssetSortColumn.Value:
                return Order(list, a => a.ValueUsd, descending);
            case AssetSortColumn.DeltaYtd:
                return Order(list, a => a.DeltaFor(DeltaPeriod.Ytd), descending);
            case AssetSortColumn.Delta3M:
                return Order(list, a => a.DeltaFor(DeltaPeriod.ThreeMonths), descending);
            case AssetSortColumn.Delta1M:
                return Order(list, a => a.DeltaFor(DeltaPeriod.OneMonth), descending);
            case AssetSortColumn.Delta1W:
                return Order(list, a => a.DeltaFor(DeltaPeriod.OneWeek), descending);
            case AssetSortColumn.Delta1D:
                return Order(list, a => a.DeltaFor(DeltaPeriod.OneDay), descending);
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static IReadOnlyList<AssetOverview> Order<TKey>(
        List<AssetOverview> list, Func<AssetOverview, TKey?> key, bool descending, IComparer<TKey>? comparer = null)
        where TKey : struct
    {
        var withValue = list.Where(a => key(a) is not null);
        var ordered = descending
            ? withValue.OrderByDescending(a => key(a)!.Value, comparer)
            : withValue.OrderBy(a => key(a)!.Value, comparer);
        return ordered.ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(list.Where(a => key(a) is null).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    private static IReadOnlyList<AssetOverview> Order(
        List<AssetOverview> list, Func<AssetOverview, string> key, bool descending, IComparer<string> comparer) =>
        (descending ? list.OrderByDescending(key, comparer) : list.OrderBy(key, comparer)).ToList();
}
