namespace CryptoPortfolioManager.Core.Entities;

/// <summary>
/// Supported blockchains. Persisted by name (string), so adding a value never requires a schema change.
/// </summary>
public enum Chain
{
    Ethereum,
    Bitcoin,
    Solana,
    BnbSmartChain,
    Polygon,
    Arbitrum,
    Optimism,
    Base,
        Avalanche,
    Cronos,
    Scroll,
    Flare,
    XrpLedger,
    Sui,
    Cardano,
    Stellar,

    /// <summary>
    /// Not a blockchain: a token held on a centralized exchange, whitelisted by ticker instead of by contract. A holding
    /// from an exchange API is matched with these by symbol and its position gets no chain (the exchange reports none).
    /// No wallet address is ever read on this "chain".
    /// </summary>
    Exchange
}

/// <summary>Chains of one family share an address format and a node protocol.</summary>
public enum ChainFamily
{
    Evm,
    Bitcoin,
    Solana,
    XrpLedger,
    Sui,
    Cardano,
    Stellar,

    /// <summary>A centralized exchange: balances come from its API, by ticker, not from an address.</summary>
    Exchange
}

public static class ChainInfo
{
    public static ChainFamily FamilyOf(Chain chain) => chain switch
    {
        Chain.Bitcoin => ChainFamily.Bitcoin,
        Chain.Solana => ChainFamily.Solana,
        // No address is ever read for an exchange token; it must never fall through to an EVM node either.
        Chain.Exchange => ChainFamily.Exchange,
        Chain.XrpLedger => ChainFamily.XrpLedger,
        Chain.Sui => ChainFamily.Sui,
        Chain.Cardano => ChainFamily.Cardano,
        Chain.Stellar => ChainFamily.Stellar,
        _ => ChainFamily.Evm
    };

    public static string Label(Chain chain) => chain switch
    {
        Chain.BnbSmartChain => "BNB Smart Chain",
        Chain.XrpLedger => "XRP Ledger",
        Chain.Exchange => "Exchange (ticker)",
        _ => chain.ToString()
    };
}
