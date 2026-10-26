using CryptoPortfolioManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPortfolioManager.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IPortfolioExportService, PortfolioExportService>();
        services.AddScoped<IPriceSyncService, PriceSyncService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IWhitelistService, WhitelistService>();
        services.AddScoped<IWalletScanService, WalletScanService>();
        services.AddScoped<IPositionHistoryService, PositionHistoryService>();
        services.AddSingleton<PriceSyncLock>();
        services.AddSingleton<WalletScanLock>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
