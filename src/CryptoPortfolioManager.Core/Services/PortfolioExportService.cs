using System.Text;

namespace CryptoPortfolioManager.Core.Services;

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

public interface IPortfolioExportService
{
    /// <summary>Exports all assets and their positions as a UTF-8 CSV file.</summary>
    Task<ExportFile> ExportCsvAsync(CancellationToken ct = default);
}

public class PortfolioExportService(IPortfolioService portfolio, TimeProvider clock) : IPortfolioExportService
{
    // UTF-8 with BOM so Excel recognises the encoding.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public async Task<ExportFile> ExportCsvAsync(CancellationToken ct = default)
    {
        var overview = await portfolio.GetPortfolioOverviewAsync(ct);
        var csv = PortfolioCsvBuilder.Build(overview);

        return new ExportFile(
            PortfolioCsvBuilder.FileName(clock.GetLocalNow()),
            "text/csv",
            [.. Utf8WithBom.GetPreamble(), .. Utf8WithBom.GetBytes(csv)]);
    }
}
