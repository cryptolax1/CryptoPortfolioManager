using CryptoPortfolioManager.Core.Abstractions;
using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Services;

public interface IPositionHistoryService
{
    Task<IReadOnlyList<PositionChangeLog>> GetRecentAsync(int take = 500, CancellationToken ct = default);
}

public class PositionHistoryService(IWalletRepository repository) : IPositionHistoryService
{
    public Task<IReadOnlyList<PositionChangeLog>> GetRecentAsync(int take = 500, CancellationToken ct = default) =>
        repository.GetChangeLogAsync(Math.Clamp(take, 1, 5000), ct);
}
