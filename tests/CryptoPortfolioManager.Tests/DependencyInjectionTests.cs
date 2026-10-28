using CryptoPortfolioManager.Core;
using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;
using CryptoPortfolioManager.Core.Services;
using CryptoPortfolioManager.Infrastructure;
using CryptoPortfolioManager.Infrastructure.Blockchain;
using CryptoPortfolioManager.Infrastructure.CoinGecko;
using CryptoPortfolioManager.Infrastructure.Exchanges;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoPortfolioManager.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void PriceSync_Graph_ResolvesWithConfiguredOptions()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CoinGecko:ApiKey"] = "secret-from-config",
            ["CoinGecko:MinRequestInterval"] = "00:00:01",
            ["CoinGecko:TimeoutSeconds"] = "12"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore();
        services.AddInfrastructure("Data Source=:memory:");
                services.AddCoinGeckoPriceProvider(config);
        services.AddBlockchainBalanceReaders(config);
        services.AddExchangeBalanceReaders(config);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        // One reader per supported exchange; the scan picks the one whose name matches a wallet's source.
        var exchanges = scope.ServiceProvider.GetServices<IExchangeBalanceReader>().ToList();
        Assert.Equal(ExchangeCatalog.All.Order(), exchanges.Select(e => e.Name).Order());

        Assert.IsType<PriceSyncService>(scope.ServiceProvider.GetRequiredService<IPriceSyncService>());
        Assert.IsType<WalletScanService>(scope.ServiceProvider.GetRequiredService<IWalletScanService>());
        Assert.IsType<WalletService>(scope.ServiceProvider.GetRequiredService<IWalletService>());
        Assert.IsType<WhitelistService>(scope.ServiceProvider.GetRequiredService<IWhitelistService>());
                var reader = Assert.IsType<MultiChainBalanceReader>(scope.ServiceProvider.GetRequiredService<IWalletBalanceReader>());
        Assert.All([Chain.Scroll, Chain.Flare, Chain.XrpLedger, Chain.Solana, Chain.Avalanche, Chain.Bitcoin, Chain.Sui, Chain.Cardano, Chain.Stellar], c => Assert.True(reader.Supports(c)));
        Assert.IsType<CoinGeckoClient>(scope.ServiceProvider.GetRequiredService<IPriceProvider>());

                var options = scope.ServiceProvider.GetRequiredService<IOptions<CoinGeckoOptions>>().Value;
        Assert.Equal("secret-from-config", options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(1), options.MinRequestInterval);
        Assert.Equal(12, options.TimeoutSeconds);
    }

    [Fact]
    public void FlareRpc_StaysConfigured_WhenConfigOnlyOverridesSomeChains()
    {
        // appsettings.json lists the chains it knows; Flare is not in it. Binding a partial RpcUrls section must not
        // drop the built-in defaults, so a chain added to the enum keeps working without touching appsettings.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:RpcUrls:Ethereum"] = "https://my-private-node.example/eth"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlockchainBalanceReaders(config);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<BlockchainRpcOptions>>().Value;

        Assert.Equal("https://my-private-node.example/eth", options.RpcUrls["Ethereum"]); // config wins
        Assert.Equal("https://flare-api.flare.network/ext/C/rpc", options.RpcUrls["Flare"]); // default kept
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CoinGecko:BaseUrl"] = "not a url"
        }).Build();

        var services = new ServiceCollection();
        services.AddCoinGeckoPriceProvider(config);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CoinGeckoOptions>>().Value);
    }
}
