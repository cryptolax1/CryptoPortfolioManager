using System.Diagnostics;

namespace CryptoPortfolioManager.Infrastructure.CoinGecko;

/// <summary>Spaces out requests across all CoinGecko calls in the process (registered as singleton).</summary>
public sealed class CoinGeckoRequestThrottle
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _lastRequestTimestamp;
    private bool _hasRequested;

    public async Task WaitAsync(TimeSpan minInterval, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_hasRequested)
            {
                var wait = minInterval - Stopwatch.GetElapsedTime(_lastRequestTimestamp);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }

            _lastRequestTimestamp = Stopwatch.GetTimestamp();
            _hasRequested = true;
        }
        finally
        {
            _lock.Release();
        }
    }
}
