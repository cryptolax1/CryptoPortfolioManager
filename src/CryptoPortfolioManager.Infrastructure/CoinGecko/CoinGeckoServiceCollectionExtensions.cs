using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.CoinGecko;

public static class CoinGeckoServiceCollectionExtensions
{
    /// <summary>Registers the CoinGecko price provider as a typed HttpClient. Settings come from the "CoinGecko" section.</summary>
    public static IServiceCollection AddCoinGeckoPriceProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CoinGeckoOptions>()
            .Bind(configuration.GetSection(CoinGeckoOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "CoinGecko:BaseUrl must be an absolute URL.")
            .Validate(o => o.TimeoutSeconds > 0, "CoinGecko:TimeoutSeconds must be greater than zero.")
            .Validate(o => o.MaxRetries >= 0, "CoinGecko:MaxRetries cannot be negative.");

        services.AddSingleton<CoinGeckoRequestThrottle>();

        services.AddHttpClient<IPriceProvider, CoinGeckoClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<CoinGeckoOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });

        return services;
    }
}
