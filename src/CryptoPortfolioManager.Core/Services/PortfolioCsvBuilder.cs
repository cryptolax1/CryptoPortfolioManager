using System.Globalization;
using System.Text;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

/// <summary>
/// Builds the portfolio CSV: one row per asset with name, symbol, identifier, total quantity, price and total value.
/// RFC 4180 escaping, comma separated, CRLF line endings. Numbers use the invariant culture (dot decimal separator)
/// and full decimal precision, no thousands separators. Missing prices are empty fields, never 0.
/// </summary>
public static class PortfolioCsvBuilder
{
    public static readonly string[] Header =
        ["Token name", "Token symbol", "Identifier", "Total quantity", "Price USD", "Total value USD"];

    public static string Build(PortfolioOverview overview)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);

        foreach (var asset in overview.Assets)
            AppendRow(sb, [
                Text(asset.Name), Text(asset.Symbol), Text(asset.CoinGeckoId),
                Number(asset.Quantity), Number(asset.PriceUsd), Number(asset.ValueUsd)
            ]);

        return sb.ToString();
    }

    /// <summary>For example <c>crypto-portfolio-2026-10-14_233815.csv</c> (local date and time).</summary>
    public static string FileName(DateTimeOffset localNow) =>
        $"crypto-portfolio-{localNow.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}.csv";

    /// <summary>Escapes a single CSV field: quotes when it contains a comma, quote or line break.</summary>
    public static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string Number(decimal? value) =>
        value is { } v ? (v / 1.000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : "";

    // Text that spreadsheets would execute as a formula is prefixed with an apostrophe (CSV injection).
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells) =>
        sb.AppendJoin(',', cells.Select(Escape)).Append("\r\n");
}
