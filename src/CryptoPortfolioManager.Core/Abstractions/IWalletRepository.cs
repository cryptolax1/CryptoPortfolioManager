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
    /// Stores the result of one on-chain wallet scan: a positive balance creates or updates the position, a zero balance
    /// removes it, and positions of tokens that are no longer whitelisted are removed. Tokens without a balance (read
    /// failed) are untouched.
    /// <paramref name="exchangeHoldings"/> is applied in the same transaction, so a wallet can be read on-chain and on
    /// an exchange in one go (an empty list leaves exchange positions untouched).
    /// </summary>
    Task<PositionChanges> ApplyScanAsync(
        int walletId, IReadOnlyCollection<TokenBalance> balances, string? error, DateTime nowUtc,
        IReadOnlyCollection<ExchangePosition> exchangeHoldings, CancellationToken ct = default);

    /// <summary>
    /// An exchange account whose balances come from the exchange's API.
    /// <paramref name="holdings"/> is the <em>complete</em> set of non-zero holdings the exchange reported: a whitelisted
    /// asset that is absent has a zero balance and its position is removed. Symbols are matched case-insensitively with
    /// the whitelist; an asset the portfolio does not know yet is created using the exchange's ticker, so the holding is
    /// visible per wallet even before it is whitelisted.
    /// </summary>
    Task<PositionChanges> ApplyExchangeScanAsync(
        int walletId, IReadOnlyCollection<ExchangePosition> holdings, string? error, DateTime nowUtc, CancellationToken ct = default);

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

