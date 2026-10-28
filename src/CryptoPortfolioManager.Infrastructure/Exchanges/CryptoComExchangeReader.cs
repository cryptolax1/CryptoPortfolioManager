using System.Globalization;
using System.Text.Json;
using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>
/// Reads the balances of a Crypto.com App account (the "Digital Wallet" API behind the developer dashboard). The
/// request is signed the way that API documents: <c>HMAC_SHA256(secret, requestId + timestamp + method + path + host)</c>,
/// sent as lower-case hex in the <c>sig</c> header. Only the read-only balances endpoint is used.
/// </summary>
public sealed class CryptoComExchangeReader(
    HttpClient http,
    IConfiguration configuration,
    ILogger<CryptoComExchangeReader> logger) : IExchangeBalanceReader
{
    public const string ExchangeName = ExchangeCatalog.CryptoCom;

    /// <summary>The App accounts endpoint; it lists one entry per wallet: fixed 70 currencies, plus token accounts.</summary>
    private const string AccountPath = "/v1/crypto/accounts";

    public string Name => ExchangeName;

    public bool IsConfigured => ExchangeCredentials.Read(configuration, Name).IsConfigured;

    public async Task<IReadOnlyList<ExchangePosition>> GetBalancesAsync(CancellationToken ct = default)
    {
        var credentials = ExchangeCredentials.Read(configuration, Name);
        if (!credentials.IsConfigured)
            throw new ExchangeBalanceException(credentials.MissingMessage());

        var requestId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var host = http.BaseAddress?.Host ?? "api.crypto.com";

        var signature = credentials.SignHex(SignedText(requestId, timestamp, HttpMethod.Get.Method, AccountPath, host));

        using var request = new HttpRequestMessage(HttpMethod.Get, AccountPath);
        request.Headers.Add("id", requestId);
        request.Headers.Add("api_key", credentials.ApiKey);
        request.Headers.Add("ts", timestamp);
        request.Headers.Add("sig", signature);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExchangeBalanceException($"Network error while contacting {Name}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExchangeBalanceException($"{Name} did not answer in time.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ExchangeBalanceException(ErrorText.ForStatus(response.StatusCode, Name, body));

            return ParseBalances(body);
        }
    }

    private IReadOnlyList<ExchangePosition> ParseBalances(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ExchangeBalanceException($"{Name} returned an unreadable response.", ex);
        }

        using (document)
        {
            // Documented format: { "result": { "data": [ { "currency": "CRO", "balance": 1.2, ... }, ... ] } }.
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new ExchangeBalanceException($"{Name} returned an unexpected response (no account list).");

            // Several accounts can share a currency (Crypto.com has separate fiat wallets); the balance is the sum.
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in data.EnumerateArray())
            {
                if (account.ValueKind != JsonValueKind.Object) continue;
                if (!account.TryGetProperty("currency", out var currencyElement) || currencyElement.ValueKind != JsonValueKind.String) continue;

                // "LD" prefixes Crypto.com's Lend products ("LDBTC" is lent-out BTC); the balance is still the holding.
                var symbol = currencyElement.GetString()!.Trim();
                if (symbol.StartsWith("LD", StringComparison.OrdinalIgnoreCase) && symbol.Length > 2) symbol = symbol[2..];
                if (symbol.Length == 0) continue;

                var balance = ReadDecimal(account, "balance");
                if (balance <= 0m) continue;

                totals[symbol] = totals.GetValueOrDefault(symbol) + balance;
            }

            logger.LogInformation("{Exchange}: read {Count} non-zero holdings", Name, totals.Count);
            return totals
                .Select(t => new ExchangePosition(t.Key, t.Key, $"exchange-{t.Key.ToLowerInvariant()}", null, null, t.Value))
                .ToList();
        }
    }

    private static decimal ReadDecimal(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value)) return 0m;

        return value.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            _ => 0m
        };
    }

    /// <summary>
    /// The text this API signs: requestId + timestamp + method + path + host. Public because it is the contract that has
    /// to stay identical between the client and any test that verifies the signature.
    /// </summary>
    public static string SignedText(string requestId, string timestamp, string method, string path, string host) =>
        requestId + timestamp + method + path + host;
}
