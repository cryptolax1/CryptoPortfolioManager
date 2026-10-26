using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Models;

public sealed record WalletInput(string Address, string Name, WalletType Type, string Source);

public sealed record WhitelistedTokenInput(string Name, string Symbol, Chain Chain, string? ContractAddress, int Decimals, string CoinGeckoId);

/// <summary>Outcome of a validated management action; <see cref="Error"/> is null on success.</summary>
public sealed record ServiceResult(string? Error = null)
{
    public bool Ok => Error is null;
    public static ServiceResult Success { get; } = new();
    public static ServiceResult Fail(string error) => new(error);
}

public sealed record TokenBalance(int WhitelistedTokenId, decimal Quantity);

public sealed record PositionChanges(int Added, int Updated, int Removed);

public sealed record WalletScanResult(
    bool AlreadyRunning,
    int WalletsScanned,
    int WalletsSkipped,
    PositionChanges Changes,
    IReadOnlyList<string> Errors)
{
    public static WalletScanResult Busy { get; } = new(true, 0, 0, new PositionChanges(0, 0, 0), []);
}
