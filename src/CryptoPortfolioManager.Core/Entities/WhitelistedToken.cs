namespace CryptoPortfolioManager.Core.Entities;

/// <summary>
/// A token the wallet scan is allowed to pick up on a given chain. Tokens that are not whitelisted are ignored.
/// Several whitelisted tokens (e.g. USDC on two chains) can point to the same <see cref="Asset"/>.
/// </summary>
public class WhitelistedToken
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Symbol { get; set; }

    public Chain Chain { get; set; }

    /// <summary>Contract address; null for the native coin of the chain (CRO on Cronos, BNB on BNB Smart Chain).</summary>
    public string? ContractAddress { get; set; }

    /// <summary>Decimals of the token, used to convert raw on-chain balances to whole tokens.</summary>
    public int Decimals { get; set; } = 18;

    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
}
