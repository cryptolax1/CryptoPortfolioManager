using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

/// <summary>Routes each request to the reader that speaks the protocol of the chain's family.</summary>
public sealed class MultiChainBalanceReader(
    EvmJsonRpcBalanceReader evm, XrpLedgerBalanceReader xrpLedger, SolanaBalanceReader solana,
    BitcoinBalanceReader bitcoin, SuiBalanceReader sui, CardanoBalanceReader cardano, StellarBalanceReader stellar) : IWalletBalanceReader
{
    public bool Supports(Chain chain) => For(chain)?.Supports(chain) ?? false;

    public Task<decimal> GetBalanceAsync(Chain chain, string walletAddress, string? contractAddress, int decimals, CancellationToken ct = default) =>
        (For(chain) ?? throw new WalletBalanceException($"Scanning {chain} is not supported."))
        .GetBalanceAsync(chain, walletAddress, contractAddress, decimals, ct);

    private IWalletBalanceReader? For(Chain chain) => ChainInfo.FamilyOf(chain) switch
    {
        ChainFamily.Evm => evm,
        ChainFamily.XrpLedger => xrpLedger,
        ChainFamily.Solana => solana,
        ChainFamily.Bitcoin => bitcoin,
        ChainFamily.Sui => sui,
                ChainFamily.Cardano => cardano,
        ChainFamily.Stellar => stellar,
        // An exchange has no node to read; its balances come from the exchange API.
        ChainFamily.Exchange => null,
        _ => null
    };
}
