using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>Base URL and timeout of an exchange's API; the API keys live in user secrets (see <see cref="ExchangeCredentials"/>).</summary>
public sealed class ExchangeApiOptions
{
    public string BitvavoBaseUrl { get; set; } = "https://api.bitvavo.com/";
    public string CryptoComBaseUrl { get; set; } = "https://api.crypto.com/";
    public int TimeoutSeconds { get; set; } = 30;
}

public static class ExchangeServiceCollectionExtensions
{
    /// <summary>
    /// Registers one reader per supported exchange as a typed HttpClient, bound to the "Exchange" configuration section.
    /// Registering both as <see cref="IExchangeBalanceReader"/> lets the scan pick the one that matches a wallet's source.
    /// </summary>
    public static IServiceCollection AddExchangeBalanceReaders(this IServiceCollection services, IConfiguration configuration)
    {
        // The readers re-read their credentials on each scan (so a key added to user secrets works without a restart),
        // which needs the configuration itself rather than a bound snapshot of the keys.
        services.AddSingleton(configuration);

        services.AddOptions<ExchangeApiOptions>()
            .Bind(configuration.GetSection("Exchange"))
            .Validate(o => Uri.TryCreate(o.BitvavoBaseUrl, UriKind.Absolute, out _), "Exchange:BitvavoBaseUrl must be an absolute URL.")
            .Validate(o => Uri.TryCreate(o.CryptoComBaseUrl, UriKind.Absolute, out _), "Exchange:CryptoComBaseUrl must be an absolute URL.")
            .Validate(o => o.TimeoutSeconds > 0, "Exchange:TimeoutSeconds must be greater than zero.");

        services.AddHttpClient<BitvavoExchangeReader>()
            .ConfigureHttpClient((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<ExchangeApiOptions>>().Value;
                client.BaseAddress = Endpoint(o.BitvavoBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });

        services.AddHttpClient<CryptoComExchangeReader>()
            .ConfigureHttpClient((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<ExchangeApiOptions>>().Value;
                client.BaseAddress = Endpoint(o.CryptoComBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });

        services.AddTransient<IExchangeBalanceReader>(sp => sp.GetRequiredService<BitvavoExchangeReader>());
        services.AddTransient<IExchangeBalanceReader>(sp => sp.GetRequiredService<CryptoComExchangeReader>());

        return services;
    }

    private static Uri Endpoint(string baseUrl) => new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
}
