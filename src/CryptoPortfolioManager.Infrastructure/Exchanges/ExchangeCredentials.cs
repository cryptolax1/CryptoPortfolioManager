using System.Security.Cryptography;
using System.Text;
using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>
/// The API credentials of one exchange, read from configuration under <c>Exchange:&lt;name&gt;:ApiKey</c> and
/// <c>Exchange:&lt;name&gt;:ApiSecret</c>. They belong in user secrets (or environment variables), never in source.
/// </summary>
internal sealed class ExchangeCredentials(string name, string apiKey, string apiSecret)
{
    public string ApiKey { get; } = apiKey;
    public string ApiSecret { get; } = apiSecret;

    /// <summary>
    /// Reads the credentials for an exchange. The values are re-read on every use (configuration is cheap and
    /// relational), so setting a key in user secrets takes effect on the next scan without a restart.
    /// </summary>
    public static ExchangeCredentials Read(IConfiguration configuration, string name)
    {
        var (keyName, secretName) = ExchangeCatalog.CredentialKeys(name);
        return new ExchangeCredentials(
            name,
            configuration[keyName]?.Trim() ?? "",
            configuration[secretName]?.Trim() ?? "");
    }

    public bool IsConfigured => ApiKey.Length > 0 && ApiSecret.Length > 0;

    /// <summary>The message to show when a key is missing, including the exact commands to set it.</summary>
    public string MissingMessage()
    {
        var (keyName, secretName) = ExchangeCatalog.CredentialKeys(name);
        return $"{name} API credentials are not configured. Set them once with:\n" +
               $"dotnet user-secrets set \"{keyName}\" \"<api key>\"\n" +
               $"dotnet user-secrets set \"{secretName}\" \"<api secret>\"\n" +
               "(run this in the CryptoPortfolioManager.Web project folder).";
    }

    /// <summary>Lower-case hex HMAC-SHA256 signature (Bitvavo signs the "METHOD /v2/path" text with the secret).</summary>
    public string SignHex(string message) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiSecret), Encoding.UTF8.GetBytes(message)));

    public byte[] SignBytes(string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiSecret), Encoding.UTF8.GetBytes(message));
}
