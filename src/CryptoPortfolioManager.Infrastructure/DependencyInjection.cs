using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPortfolioManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<IPortfolioRepository, PortfolioRepository>();
        services.AddSingleton<IPriceSyncRepository, PriceSyncRepository>();
        services.AddSingleton<IWalletRepository, WalletRepository>();
        return services;
    }
}
