using System.Net;

namespace CryptoPortfolioManager.Infrastructure.Exchanges;

/// <summary>
/// A piece of text taken from an error response, safe to show in the user interface: line breaks and repeated spaces
/// are collapsed and the result is capped, so a whole HTML error page never ends up in a wallet's last-error field.
/// </summary>
internal static class ErrorText
{
    public static string Snippet(string? body, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    /// <summary>Human-readable message for a rejected HTTP status, shared by the exchange clients.</summary>
    public static string ForStatus(HttpStatusCode status, string exchange, string? body)
    {
        var detail = Snippet(body);
        var suffix = detail.Length > 0 ? $" {detail}" : "";

        return status switch
        {
            HttpStatusCode.Unauthorized => $"{exchange} rejected the API key or signature (HTTP 401).{suffix}",
            HttpStatusCode.Forbidden => $"{exchange} denied access to the account (HTTP 403); does the key have read permission?{suffix}",
            HttpStatusCode.NotFound => $"{exchange} does not know this endpoint or account (HTTP 404).{suffix}",
            HttpStatusCode.TooManyRequests => $"{exchange} rate limit exceeded (HTTP 429); try again later.{suffix}",
            >= HttpStatusCode.InternalServerError => $"{exchange} server error (HTTP {(int)status}).{suffix}",
            _ => $"{exchange} rejected the request (HTTP {(int)status}).{suffix}"
        };
    }
}
