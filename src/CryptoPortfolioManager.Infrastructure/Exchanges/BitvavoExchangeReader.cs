using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoPortfolioManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>
/// Reads the balances of a Bitvavo account through its private REST API (<c>GET /v2/balance</c>). Only the documented
/// balance endpoint is used; no withdrawal or trading call is ever made, so a read-only key is enough.
/// <para>
/// The signature follows Bitvavo's reference client exactly: the text that is signed is
/// <c>timestamp + method + "/v2" + endpoint + body</c> (empty body for GET), HMAC-SHA256 with the secret, hex encoded.
/// Signing just "GET /v2/balance" is not enough: the timestamp is part of the signed text, so a forgotten timestamp or a
/// different concatenation order makes Bitvavo answer "errorCode 309, the signature is invalid".
/// </para>
/// </summary>
public sealed class BitvavoExchangeReader(
    HttpClient http,
    IConfiguration configuration,
    ILogger<BitvavoExchangeReader> logger) : IExchangeBalanceReader
{
    public const string ExchangeName = ExchangeCatalog.Bitvavo;
    private const string BalancePath = "/v2/balance";
    private const string ApiPrefix = "/v2";

    /// <summary>How long Bitvavo accepts a request after its timestamp (the default of the reference client).</summary>
    private const int AccessWindowMs = 10000;

    /// <summary>
    /// The text Bitvavo signs: timestamp + method + "/v2" + endpoint + body (empty for GET). Public so a test pins the
    /// exact format; it is the part that produced "errorCode 309, the signature is invalid" when it was wrong.
    /// </summary>
    public static string SignedText(string timestamp, string method, string endpoint, string body) =>
        timestamp + method + ApiPrefix + endpoint + body;

    public string Name => ExchangeName;

    public bool IsConfigured => ExchangeCredentials.Read(configuration, Name).IsConfigured;

    public async Task<IReadOnlyList<ExchangePosition>> GetBalancesAsync(CancellationToken ct = default)
    {
        var credentials = ExchangeCredentials.Read(configuration, Name);
        if (!credentials.IsConfigured)
            throw new ExchangeBalanceException(credentials.MissingMessage());

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var stringToSign = timestamp+ HttpMethod.Get.Method+ BalancePath+ "";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(credentials.ApiSecret));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        string signature = Convert.ToHexString(hashBytes).ToLower();

        using var request = new HttpRequestMessage(HttpMethod.Get, BalancePath);
        request.Headers.Add("Bitvavo-Access-Key", credentials.ApiKey);
        request.Headers.Add("Bitvavo-Access-Timestamp", timestamp);
        request.Headers.Add("Bitvavo-Access-Window", AccessWindowMs.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Bitvavo-Access-Signature", signature);
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
                throw new ExchangeBalanceException(DescribeFailure(response.StatusCode, body));

            return ParseBalances(body);
        }
    }

    /// <summary>
    /// Turns a failed response into a message that names the actual cause. Bitvavo answers 403 both for a rejected key
    /// and for a bad signature (errorCode 309), so the generic "does the key have read permission?" is wrong here; the
    /// API's own errorCode and error text are the useful part.
    /// </summary>
    private string DescribeFailure(HttpStatusCode status, string body)
    {
        var code = ReadErrorField(body, "errorCode");
        var error = ReadErrorField(body, "error");
        var detail = string.Join(" ", new[] { code is null ? null : $"errorCode {code}", error }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var suffix = detail.Length > 0 ? $" Bitvavo says: {detail}." : $" {ErrorText.Snippet(body)}";

        return code switch
        {
            "309" => $"{Name} rejected the signature (HTTP {(int)status}, errorCode 309).{suffix} " +
                     "Check that the API secret is complete and belongs to this API key, and that the PC clock is correct.",
            "305" or "306" => $"{Name} rejected the API key or timestamp (HTTP {(int)status}).{suffix}",
            _ when status == HttpStatusCode.Forbidden => $"{Name} denied the request (HTTP 403).{suffix} " +
                     "Does the API key have the 'view' (read) permission enabled?",
            _ => ErrorText.ForStatus(status, Name, body)
        };
    }

    private static string? ReadErrorField(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(property, out var value)
                ? value.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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

                holdings.Add(new ExchangePosition(symbol, symbol, ExchangePosition.PlaceholderId(symbol), null, null, quantity));
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
