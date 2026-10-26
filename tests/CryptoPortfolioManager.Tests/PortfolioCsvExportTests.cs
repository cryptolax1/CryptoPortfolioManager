using System.Text;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;
using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace CryptoPortfolioManager.Tests;

public class PortfolioCsvExportTests
{
    private static readonly IReadOnlyDictionary<DeltaPeriod, decimal?> NoDeltas = new Dictionary<DeltaPeriod, decimal?>();

    private static AssetOverview Asset(string name, string symbol, string id, decimal quantity, decimal? price,
        params PositionLine[] positions) =>
        new(1, name, symbol, id, quantity, price, price * quantity, NoDeltas, positions);

    private static PositionLine Position(string wallet, Chain? chain, string? address, decimal quantity) =>
        new(1, wallet, chain, address, quantity, null);

    /// <summary>Minimal RFC 4180 parser, independent of the builder's escaping code.</summary>
    private static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
                i++;
            }
            else field.Append(c);
        }

        Assert.False(quoted, "Unterminated quoted field");
        Assert.Empty(field.ToString());
        Assert.Empty(row);
        return rows;
    }

    [Fact]
    public void Header_HasOnlyAssetColumns_WithIdentifier()
    {
        var rows = Parse(PortfolioCsvBuilder.Build(new PortfolioOverview([])));

        var header = Assert.Single(rows);
        Assert.Equal(
            ["Token name", "Token symbol", "Identifier", "Total quantity", "Price USD", "Total value USD"], header);
    }

    [Fact]
    public void OneRowPerAsset_PositionsAndDeltasAreNotExported()
    {
        var overview = new PortfolioOverview([
            new AssetOverview(1, "Ethereum", "ETH", "ethereum", 5.85m, 2500m, 5.85m * 2500m,
                new Dictionary<DeltaPeriod, decimal?> { [DeltaPeriod.OneDay] = 0.0203m },
                [Position("Ledger", Chain.Ethereum, "0x1", 3.44m), Position("Bitvavo", null, null, 2.41m)]),
            Asset("Bitcoin", "BTC", "bitcoin", 0.35m, 62000m)
        ]);

        var csv = PortfolioCsvBuilder.Build(overview);
        var rows = Parse(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Ethereum", "ETH", "ethereum", "5.85", "2500", "14625"], rows[1]);
        Assert.Equal(["Bitcoin", "BTC", "bitcoin", "0.35", "62000", "21700"], rows[2]);
        Assert.DoesNotContain("Ledger", csv);
        Assert.DoesNotContain("Bitvavo", csv);
        Assert.DoesNotContain("0.0203", csv);
    }

    [Fact]
    public void AssetWithoutPositions_IsStillExported()
    {
        var overview = new PortfolioOverview([Asset("Ethereum", "ETH", "ethereum", 0m, null)]);

        var rows = Parse(PortfolioCsvBuilder.Build(overview));

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Ethereum", "ETH", "ethereum", "0", "", ""], rows[1]);
    }

    [Fact]
    public void MissingPrice_IsEmpty_NotZero()
    {
        var row = Parse(PortfolioCsvBuilder.Build(new PortfolioOverview([Asset("Solana", "SOL", "solana", 2m, null)])))[1];

        Assert.Equal("2", row[3]);
        Assert.Equal("", row[4]);
        Assert.Equal("", row[5]);
    }

    [Fact]
    public void CommasQuotesAndLineBreaks_AreEscapedAndRoundTrip()
    {
        const string name = "Wrapped \"Coin\", v2";
        const string id = "wrapped,\"coin\"\nline2";
        var csv = PortfolioCsvBuilder.Build(new PortfolioOverview([Asset(name, "WC", id, 1m, 1m)]));
        var rows = Parse(csv);

        Assert.Contains("\"Wrapped \"\"Coin\"\", v2\"", csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(name, rows[1][0]);
        Assert.Equal(id, rows[1][2]);
        Assert.All(rows, r => Assert.Equal(6, r.Count));
    }

    [Fact]
    public void EmptyText_BecomesEmptyField()
    {
        var row = Parse(PortfolioCsvBuilder.Build(new PortfolioOverview([Asset("Token", "", "", 1m, 1m)])))[1];

        Assert.Equal("", row[1]);
        Assert.Equal("", row[2]);
    }

    [Fact]
    public void Numbers_UseInvariantCulture_WithFullPrecision()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
        try
        {
            var overview = new PortfolioOverview([
                Asset("Shiba", "SHIB", "shiba-inu", 123456789.123456789012345678m, 0.000000012345678901m)
            ]);

            var row = Parse(PortfolioCsvBuilder.Build(overview))[1];

            Assert.Equal("123456789.123456789012345678", row[3]);
            Assert.Equal("0.000000012345678901", row[4]);
            Assert.DoesNotContain(row, c => c.Contains(','));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+1")]
    [InlineData("@SUM(A1)")]
    public void FormulaLikeText_IsNeutralised(string text)
    {
        var row = Parse(PortfolioCsvBuilder.Build(new PortfolioOverview([Asset(text, "X", "x", 1m, 1m)])))[1];

        Assert.Equal("'" + text, row[0]);
    }

    [Fact]
    public void Lines_EndWithCrLf()
    {
        var csv = PortfolioCsvBuilder.Build(new PortfolioOverview([Asset("A", "A", "a", 1m, 1m)]));

        Assert.EndsWith("\r\n", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void FileName_ContainsLocalDateAndTime()
    {
        var name = PortfolioCsvBuilder.FileName(new DateTimeOffset(2026, 10, 14, 23, 38, 5, TimeSpan.FromHours(2)));

        Assert.Equal("crypto-portfolio-2026-10-14_233805.csv", name);
    }

    private sealed class StubPortfolio(PortfolioOverview overview) : IPortfolioService
    {
        public Task<PortfolioOverview> GetPortfolioOverviewAsync(CancellationToken ct = default) => Task.FromResult(overview);
    }

    [Fact]
    public async Task ExportService_ReturnsUtf8CsvWithBomAndLocalTimestampInName()
    {
        var overview = new PortfolioOverview([Asset("Ether – Ξ café", "ETH", "ethereum", 1m, 1m)]);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 22, 30, 15, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));

        var file = await new PortfolioExportService(new StubPortfolio(overview), clock)
            .ExportCsvAsync(TestContext.Current.CancellationToken);

        Assert.Equal("crypto-portfolio-2026-10-15_003015.csv", file.FileName);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.Content.Take(3));

        var text = new UTF8Encoding(false).GetString(file.Content, 3, file.Content.Length - 3);
        Assert.Contains("Ether – Ξ café", text);
        Assert.StartsWith("Token name,Token symbol,Identifier,", text);
    }
}
