using System.Text.RegularExpressions;

namespace CryptoPortfolioManager.Core.Services;

public static partial class EvmAddress
{
    /// <summary>0x followed by 40 hex characters. The EIP-55 checksum casing is not enforced.</summary>
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex Pattern();
}
