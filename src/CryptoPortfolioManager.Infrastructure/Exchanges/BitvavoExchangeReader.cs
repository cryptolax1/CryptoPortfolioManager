using System.Text.Json;
using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>
/// Reads the balances of a Bitvavo account through its private REST API (<c>GET /v2/balance</c>, signed with
/// HMAC-SHA256 over "GET /v2/balance" using the API secret). Only the documented balance endpoint is used; no
/// withdrawal or trading call is ever made, so a read-only key is enough.
/// </summary>
public sealed class BitvavoExchangeReader(
    HttpClient http,
    IConfiguration configuration,
    ILogger<BitvavoExchangeReader> logger) : IExchangeBalanceReader
{
    public const string ExchangeName = ExchangeCatalog.Bitvavo;
    private const string BalancePath = "/v2/balance";

    public string Name => ExchangeName;

    public bool IsConfigured => ExchangeCredentials.Read(configuration, Name).IsConfigured;

    public async Task<IReadOnlyList<ExchangePosition>> GetBalancesAsync(CancellationToken ct = default)
    {
        var credentials = ExchangeCredentials.Read(configuration, Name);
        if (!credentials.IsConfigured)
            throw new ExchangeBalanceException(credentials.MissingMessage());

        using var request = new HttpRequestMessage(HttpMethod.Get, BalancePath);
        request.Headers.Add("Bitvavo-Access-Key", credentials.ApiKey);
        request.Headers.Add("Bitvavo-Access-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        request.Headers.Add("Bitvavo-Access-Signature", credentials.SignHex($"GET {BalancePath}"));
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
            // Documented format: [ { "symbol": "BTC", "available": "1.5", "inOrder": "0.2" }, ... ].
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExchangeBalanceException($"{Name} returned an unexpected response (no balance list).");

            var holdings = new List<ExchangePosition>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("symbol", out var symbolElement) || symbolElement.ValueKind != JsonValueKind.String) continue;

                var symbol = symbolElement.GetString()!.Trim();
                if (symbol.Length == 0) continue;

                // "inOrder" is money locked in an open order: it still belongs to the account.
                var quantity = ReadDecimal(entry, "available") + ReadDecimal(entry, "inOrder");
                if (quantity <= 0m) continue;

                holdings.Add(new ExchangePosition(symbol, symbol, $"exchange-{symbol.ToLowerInvariant()}", null, null, quantity));
            }

            logger.LogInformation("{Exchange}: read {Count} non-zero holdings", Name, holdings.Count);
            return holdings;
        }
    }

    private static decimal ReadDecimal(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value)) return 0m;

        return value.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            _ => 0m
        };
    }
}
