namespace CryptoPortfolioManager.Core.Abstractions;

/// <summary>A current USD price. <see cref="TimestampUtc"/> is when the provider last updated it, if known.</summary>
public sealed record PriceQuote(decimal PriceUsd, DateTime? TimestampUtc);

public sealed record PricePoint(DateTime TimestampUtc, decimal PriceUsd);

public enum PriceProviderErrorKind
{
    NotConfigured,
    Unauthorized,
    PlanRestricted,
    RateLimited,
    Timeout,
    Network,
    ServerError,
    NotFound,
    BadRequest,
    BadResponse
}

public class PriceProviderException(
    PriceProviderErrorKind kind,
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null) : Exception(message, innerException)
{
    public PriceProviderErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>True when further requests in the same run are pointless (bad key, plan limits, rate limit).</summary>
    public bool StopsRun => Kind is PriceProviderErrorKind.NotConfigured
        or PriceProviderErrorKind.Unauthorized
        or PriceProviderErrorKind.PlanRestricted
        or PriceProviderErrorKind.RateLimited;
}

/// <summary>Source of USD prices. Implementations return only valid (finite, greater than zero) values.</summary>
public interface IPriceProvider
{
    /// <summary>Value stored in <c>PriceSnapshot.Source</c>.</summary>
    string SourceName { get; }

    /// <summary>
    /// Current USD prices for the given provider ids in as few calls as possible.
    /// Ids without a valid price are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, PriceQuote>> GetCurrentUsdPricesAsync(
        IReadOnlyCollection<string> providerIds, CancellationToken ct = default);

    /// <summary>Historical USD prices for one id in the given range (UTC), oldest first.</summary>
    Task<IReadOnlyList<PricePoint>> GetHistoricalUsdPricesAsync(
        string providerId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
