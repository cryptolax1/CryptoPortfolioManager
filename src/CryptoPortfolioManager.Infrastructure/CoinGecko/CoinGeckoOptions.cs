namespace CryptoPortfolioManager.Infrastructure.CoinGecko;

public sealed class CoinGeckoOptions
{
    public const string SectionName = "CoinGecko";

    /// <summary>Demo plan root URL. Pro keys would need https://pro-api.coingecko.com/api/v3/ and a different header.</summary>
    public string BaseUrl { get; set; } = "https://api.coingecko.com/api/v3/";

    /// <summary>Demo API key. Set via user secrets (CoinGecko:ApiKey) or the CoinGecko__ApiKey environment variable; never in source.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Retries for 429, 5xx, timeouts and network errors (on top of the first attempt).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base delay for exponential backoff when the server gives no Retry-After.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest Retry-After we are willing to wait; a longer one fails the run immediately.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum time between two requests. 2.5 s is about 24 calls/min, well below the Demo limit of 100/min.</summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(2500);
}
