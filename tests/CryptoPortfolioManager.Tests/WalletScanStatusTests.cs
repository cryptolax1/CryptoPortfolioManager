using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Web.Components.Portfolio;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoPortfolioManager.Tests;

public class WalletScanStatusTests
{
    private static Wallet ExchangeWallet() => new()
    {
        Name = "Bitvavo", Type = WalletType.Cex, Source = "Bitvavo", Address = ""
    };

    private static async Task<string> RenderAsync(Wallet wallet, bool showLabel = false)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WalletScanStatus.Wallet)] = wallet,
                [nameof(WalletScanStatus.ShowLabel)] = showLabel
            });
            var output = await renderer.RenderComponentAsync<WalletScanStatus>(parameters);
            return output.ToHtmlString();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeScanStatus_ShowsSavedSuccessfulScan(bool showLabel)
    {
        var wallet = ExchangeWallet();
        wallet.LastScannedAtUtc = new DateTime(2026, 10, 29, 22, 56, 0, DateTimeKind.Utc);

        var html = await RenderAsync(wallet, showLabel);

        Assert.Contains(Format.Timestamp(wallet.LastScannedAtUtc.Value), html);
        Assert.DoesNotContain("never", html);
        Assert.DoesNotContain("not scanned", html);
        if (showLabel) Assert.Contains("last scan:", html);
    }

    [Fact]
    public async Task ExchangeScanStatus_ShowsNeverBeforeFirstScan()
    {
        var html = await RenderAsync(ExchangeWallet());
        Assert.Contains("never", html);
        Assert.DoesNotContain("not scanned", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeScanStatus_ShowsFailureAndLastSuccess(bool hasPreviousSuccess)
    {
        var wallet = ExchangeWallet();
        wallet.LastScanError = "API request failed";
        if (hasPreviousSuccess) wallet.LastScannedAtUtc = new DateTime(2026, 10, 29, 22, 56, 0, DateTimeKind.Utc);

        var html = await RenderAsync(wallet);

        Assert.Contains("last scan failed", html);
        Assert.Contains("API request failed", html);
        Assert.Contains(hasPreviousSuccess ? "last ok " + Format.Timestamp(wallet.LastScannedAtUtc!.Value) : "never succeeded", html);
    }

    [Fact]
    public async Task ScanStatus_IdentifiesUnsupportedWallets()
    {
        var wallet = ExchangeWallet();
        wallet.Source = "Unsupported exchange";
        Assert.Contains("not a supported exchange", await RenderAsync(wallet));

        wallet.Type = WalletType.Hot;
        Assert.Contains("no on-chain address", await RenderAsync(wallet));
    }
}
