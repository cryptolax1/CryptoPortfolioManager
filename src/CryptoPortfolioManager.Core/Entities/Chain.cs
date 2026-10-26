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
    XrpLedger,
    Sui,
    Cardano,
    Stellar
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
    Stellar
}

public static class ChainInfo
{
    public static ChainFamily FamilyOf(Chain chain) => chain switch
    {
        Chain.Bitcoin => ChainFamily.Bitcoin,
        Chain.Solana => ChainFamily.Solana,
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
        _ => chain.ToString()
    };
}
