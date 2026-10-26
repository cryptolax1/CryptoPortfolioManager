using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Infrastructure.Blockchain;

public static class BlockchainServiceCollectionExtensions
{
    /// <summary>Registers the balance readers (EVM, XRP Ledger, Solana, Bitcoin, Sui, Cardano, Stellar) as typed HttpClients. Settings come from the "Blockchain" section.</summary>
    public static IServiceCollection AddBlockchainBalanceReaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BlockchainRpcOptions>()
            .Bind(configuration.GetSection(BlockchainRpcOptions.SectionName))
            .Validate(o => o.TimeoutSeconds > 0, "Blockchain:TimeoutSeconds must be greater than zero.")
            .Validate(o => o.MaxRetries >= 0, "Blockchain:MaxRetries cannot be negative.")
            .Validate(o => o.RpcUrls.Values.All(u => Uri.TryCreate(u, UriKind.Absolute, out _)), "Blockchain:RpcUrls must contain absolute URLs.");

        static void Configure(IServiceProvider sp, HttpClient client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<BlockchainRpcOptions>>().Value.TimeoutSeconds);

        services.AddHttpClient<EvmJsonRpcBalanceReader>(Configure);
        services.AddHttpClient<XrpLedgerBalanceReader>(Configure);
        services.AddHttpClient<SolanaBalanceReader>(Configure);
        services.AddHttpClient<BitcoinBalanceReader>(Configure);
        services.AddHttpClient<SuiBalanceReader>(Configure);
        services.AddHttpClient<CardanoBalanceReader>(Configure);
        services.AddHttpClient<StellarBalanceReader>(Configure);
        services.AddTransient<IWalletBalanceReader, MultiChainBalanceReader>();

        return services;
    }
}
