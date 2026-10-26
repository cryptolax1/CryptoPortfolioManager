using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Abstractions;

/// <summary>Reads on-chain balances. Implementations live in Infrastructure (JSON-RPC, no scraping).</summary>
public interface IWalletBalanceReader
{
    /// <summary>True when a node for the chain is configured.</summary>
    bool Supports(Chain chain);

    /// <summary>Balance in whole tokens (already divided by 10^decimals). A null contract address means the native coin.</summary>
    /// <exception cref="WalletBalanceException">The balance could not be read reliably.</exception>
    Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default);
}

/// <summary>A balance could not be read (network error, time-out, rate limit, invalid response).</summary>
public class WalletBalanceException(string message, Exception? inner = null) : Exception(message, inner);
