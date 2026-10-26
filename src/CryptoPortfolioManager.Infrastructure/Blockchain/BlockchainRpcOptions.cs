namespace CryptoPortfolioManager.Infrastructure.Blockchain;

public sealed class BlockchainRpcOptions
{
    public const string SectionName = "Blockchain";

    /// <summary>
    /// Node endpoint per chain, keyed by the <c>Chain</c> enum name. EVM chains use JSON-RPC, XrpLedger the rippled
    /// JSON-RPC API, Solana its JSON-RPC API, Sui its GraphQL API, and Bitcoin (Esplora), Cardano (Koios) and Stellar
    /// (Horizon) a REST API. Defaults are public endpoints that need no API key.
    /// Override or add chains in appsettings (Blockchain:RpcUrls:Ethereum) or user secrets (e.g. for a private node).
    /// </summary>
    public Dictionary<string, string> RpcUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cronos"] = "https://evm.cronos.org",
        ["BnbSmartChain"] = "https://bsc-dataseed.binance.org",
        ["Ethereum"] = "https://ethereum-rpc.publicnode.com",
        ["Polygon"] = "https://polygon-bor-rpc.publicnode.com",
        ["Arbitrum"] = "https://arb1.arbitrum.io/rpc",
        ["Optimism"] = "https://mainnet.optimism.io",
        ["Base"] = "https://mainnet.base.org",
        ["Avalanche"] = "https://api.avax.network/ext/bc/C/rpc",
        ["Scroll"] = "https://rpc.scroll.io",
        ["XrpLedger"] = "https://xrplcluster.com",
        ["Solana"] = "https://api.mainnet-beta.solana.com",
        ["Bitcoin"] = "https://mempool.space/api",
        ["Sui"] = "https://graphql.mainnet.sui.io/graphql",
        ["Cardano"] = "https://api.koios.rest/api/v1",
        ["Stellar"] = "https://horizon.stellar.org"
    };

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Retries for 429, 5xx, time-outs and network errors (on top of the first attempt).</summary>
    public int MaxRetries { get; set; } = 2;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
