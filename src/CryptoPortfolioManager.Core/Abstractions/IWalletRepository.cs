using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Abstractions;

public interface IWalletRepository
{
    Task<IReadOnlyList<Wallet>> GetWalletsAsync(CancellationToken ct = default);
    Task<Wallet> AddWalletAsync(Wallet wallet, CancellationToken ct = default);

    /// <summary>Updates address, name, type and source. Scanned positions of the wallet are removed when the address changes (the next scan recreates them).</summary>
    Task UpdateWalletAsync(Wallet wallet, CancellationToken ct = default);

    /// <summary>Deletes the wallet and the positions that were read from it. Manual positions are never touched.</summary>
    Task DeleteWalletAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<WhitelistedToken>> GetWhitelistAsync(CancellationToken ct = default);

    /// <summary>Adds the token; its asset is looked up by CoinGecko ID and created when it does not exist yet.</summary>
    Task<WhitelistedToken> AddWhitelistedTokenAsync(WhitelistedToken token, string coinGeckoId, CancellationToken ct = default);
    Task UpdateWhitelistedTokenAsync(WhitelistedToken token, string coinGeckoId, CancellationToken ct = default);
    Task DeleteWhitelistedTokenAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Stores the result of one wallet scan: a positive balance creates or updates the position, a zero balance removes it,
    /// and positions of tokens that are no longer whitelisted are removed. Tokens without a balance (read failed) are untouched.
    /// </summary>
    Task<PositionChanges> ApplyScanAsync(int walletId, IReadOnlyCollection<TokenBalance> balances, string? error, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Removes every position that no wallet owns (manual, exchange or legacy rows) so the scan result is the current view.
    /// Each removal is logged. Returns the number of removed positions.
    /// </summary>
    Task<int> RemoveUnmatchedPositionsAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Positions read from wallets (with their asset), ordered by asset symbol.</summary>
    Task<IReadOnlyList<Position>> GetWalletPositionsAsync(CancellationToken ct = default);

    /// <summary>Newest-first history of added, changed and removed positions.</summary>
    Task<IReadOnlyList<PositionChangeLog>> GetChangeLogAsync(int take, CancellationToken ct = default);
}
