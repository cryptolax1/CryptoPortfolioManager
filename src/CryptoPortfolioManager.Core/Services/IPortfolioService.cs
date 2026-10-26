using CryptoPortfolioManager.Core.Models;

namespace CryptoPortfolioManager.Core.Services;

public interface IPortfolioService
{
    Task<PortfolioOverview> GetPortfolioOverviewAsync(CancellationToken ct = default);
}
