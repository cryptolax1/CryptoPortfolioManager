using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CryptoPortfolioManager.Core.Entities;

namespace CryptoPortfolioManager.Core.Services;

/// <summary>Format validation for wallet and token addresses per chain family. Nothing here talks to a node.</summary>
public static partial class BlockchainAddress
{
    private const string BitcoinAlphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string RippleAlphabet = "rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz";

    /// <summary>The family a wallet address belongs to, or null when it matches none of the supported formats.</summary>
    public static ChainFamily? DetectFamily(string? address)
    {
        if (EvmAddress.IsValid(address)) return ChainFamily.Evm;
        if (IsBitcoinAddress(address)) return ChainFamily.Bitcoin;
        if (IsXrpLedgerAccount(address)) return ChainFamily.XrpLedger;
        if (IsStellarAccount(address)) return ChainFamily.Stellar;
        if (IsCardanoAddress(address)) return ChainFamily.Cardano;
        if (IsSuiAddress(address)) return ChainFamily.Sui;
        if (IsSolanaAddress(address)) return ChainFamily.Solana;
        return null;
    }

    /// <summary>
    /// Bitcoin address of any mainnet type: native SegWit (<c>bc1…</c>), nested SegWit / P2SH (<c>3…</c>) or legacy
    /// P2PKH (<c>1…</c>). Checksums are verified, so a mistyped address is rejected.
    /// </summary>
    public static bool IsBitcoinAddress(string? value) => IsBitcoinSegwitAddress(value) || IsBitcoinBase58Address(value);

    private static bool IsBitcoinBase58Address(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 26 or > 35 || value[0] is not ('1' or '3')) return false;
        var bytes = DecodeBase58(value, BitcoinAlphabet);
        if (bytes is not { Length: 25 } || bytes[0] != (value[0] == '1' ? 0x00 : 0x05)) return false;

        var hash = SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, 21)));
        return hash.AsSpan(0, 4).SequenceEqual(bytes.AsSpan(21, 4));
    }

    /// <summary>
    /// Bitcoin native SegWit address (<c>bc1…</c>): bech32 for witness v0 (P2WPKH, P2WSH) and bech32m for taproot
    /// (witness v1).
    /// </summary>
    public static bool IsBitcoinSegwitAddress(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 90 || !TryDecodeBech32(value, out var hrp, out var data, out var isBech32m) || hrp != "bc" || data.Length < 1)
            return false;

        var program = ConvertBits(data.AsSpan(1), 5, 8);
        if (program is null) return false;

        return data[0] switch
        {
            0 => !isBech32m && program.Length is 20 or 32,
            1 => isBech32m && program.Length == 32,
            _ => false
        };
    }

    /// <summary>
    /// Cardano Shelley mainnet address: a payment address (<c>addr1…</c>) or a stake address (<c>stake1…</c>), both
    /// bech32 with a header byte that carries the address type and the network. Byron addresses are not supported.
    /// </summary>
    public static bool IsCardanoAddress(string? value)
    {
        if (string.IsNullOrEmpty(value) || !TryDecodeBech32(value, out var hrp, out var data, out var isBech32m, maxLength: 120) || isBech32m)
            return false;

        var bytes = ConvertBits(data, 5, 8);
        if (bytes is null || bytes.Length < 29) return false;

        var type = bytes[0] >> 4;
        var mainnet = (bytes[0] & 0x0F) == 1;
        return mainnet && hrp switch
        {
            "addr" => type <= 7,
            "stake" => type is 0xE or 0xF,
            _ => false
        };
    }

    /// <summary>Sui address: <c>0x</c> followed by 64 hexadecimal characters (32 bytes).</summary>
    public static bool IsSuiAddress(string? value) => value is not null && SuiAddressPattern().IsMatch(value);

    /// <summary>
    /// Stellar account (<c>G…</c>): 56 characters of base32 that decode to a version byte, the 32-byte ed25519 key and a
    /// CRC16-XModem checksum.
    /// </summary>
    public static bool IsStellarAccount(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 56 || value[0] != 'G') return false;

        var bytes = DecodeBase32(value);
        if (bytes is not { Length: 35 } || bytes[0] != 6 << 3) return false;

        var crc = Crc16Xmodem(bytes.AsSpan(0, 33));
        return bytes[33] == (crc & 0xFF) && bytes[34] == crc >> 8;
    }

    /// <summary>
    /// Sui coin type, written as <c>0xPACKAGE::module::Name</c>. The native coin (empty token address) is
    /// <c>0x2::sui::SUI</c>.
    /// </summary>
    public static bool IsSuiCoinType(string? value) => value is not null && SuiCoinTypePattern().IsMatch(value);

    /// <summary>
    /// Cardano native asset, written as <c>policyId</c> (56 hexadecimal characters) optionally followed by
    /// <c>.assetNameHex</c> (the asset name in hex, at most 32 bytes).
    /// </summary>
    public static bool TryParseCardanoAsset(string? value, out string policyId, out string assetName)
    {
        policyId = assetName = "";
        if (string.IsNullOrEmpty(value) || !CardanoAssetPattern().IsMatch(value)) return false;

        var dot = value.IndexOf('.');
        policyId = (dot < 0 ? value : value[..dot]).ToLowerInvariant();
        assetName = dot < 0 ? "" : value[(dot + 1)..].ToLowerInvariant();
        return true;
    }

    /// <summary>Stellar issued asset, written as <c>CODE:issuer</c>: a 1 to 12 character alphanumeric code and the issuer's account.</summary>
    public static bool TryParseStellarAsset(string? value, out string code, out string issuer)
    {
        code = issuer = "";
        if (string.IsNullOrEmpty(value)) return false;

        var colon = value.IndexOf(':');
        if (colon <= 0) return false;

        code = value[..colon];
        issuer = value[(colon + 1)..];
        return StellarAssetCode().IsMatch(code) && IsStellarAccount(issuer);
    }

    /// <summary>
    /// XRP Ledger classic address: base58 (Ripple alphabet), 25 bytes: version 0, 20-byte account id, 4-byte
    /// double-SHA256 checksum. The checksum makes a mistyped address detectable and avoids confusion with Solana.
    /// </summary>
    public static bool IsXrpLedgerAccount(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 25 or > 35 || value[0] != 'r') return false;
        var bytes = DecodeBase58(value, RippleAlphabet);
        if (bytes is not { Length: 25 } || bytes[0] != 0) return false;

        var hash = SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, 21)));
        return hash.AsSpan(0, 4).SequenceEqual(bytes.AsSpan(21, 4));
    }

    /// <summary>Solana public key / mint address: base58 that decodes to exactly 32 bytes.</summary>
    public static bool IsSolanaAddress(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length is >= 32 and <= 44 && DecodeBase58(value, BitcoinAlphabet) is { Length: 32 };

    /// <summary>
    /// XRP Ledger issued token, written as <c>CURRENCY.issuer</c>: a 3-character code (or 40 hex characters)
    /// and the classic address of the issuer.
    /// </summary>
    public static bool TryParseXrpLedgerToken(string? value, out string currency, out string issuer)
    {
        currency = issuer = "";
        if (string.IsNullOrEmpty(value)) return false;

        var dot = value.IndexOf('.');
        if (dot <= 0) return false;

        currency = value[..dot];
        issuer = value[(dot + 1)..];
        return CurrencyCode().IsMatch(currency) && IsXrpLedgerAccount(issuer);
    }

    /// <summary>Validates a token address for the chain; the native coin (null) is always valid.</summary>
    public static bool IsValidTokenAddress(Chain chain, string? address) => address is null || ChainInfo.FamilyOf(chain) switch
    {
        ChainFamily.Evm => EvmAddress.IsValid(address),
        ChainFamily.Solana => IsSolanaAddress(address),
        ChainFamily.XrpLedger => TryParseXrpLedgerToken(address, out _, out _),
        ChainFamily.Sui => IsSuiCoinType(address),
        ChainFamily.Cardano => TryParseCardanoAsset(address, out _, out _),
        ChainFamily.Stellar => TryParseStellarAsset(address, out _, out _),
        _ => false
    };

    public static string TokenAddressHint(Chain chain) => ChainInfo.FamilyOf(chain) switch
    {
        ChainFamily.Solana => "the mint address (base58)",
        ChainFamily.XrpLedger => "CURRENCY.issuer, for example USD.rXXXX…",
        ChainFamily.Sui => "the coin type, for example 0xPACKAGE::module::NAME",
        ChainFamily.Cardano => "policyId.assetNameHex (the policy ID is 56 hexadecimal characters)",
        ChainFamily.Stellar => "CODE:issuer, for example USDC:GXXXX…",
        ChainFamily.Bitcoin => "none: Bitcoin has only its native coin",
        _ => "0x followed by 40 hexadecimal characters"
    };

    private static byte[]? DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in value)
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0) return null;
            buffer = buffer << 5 | digit;
            bits += 5;
            if (bits < 8) continue;

            bits -= 8;
            result.Add((byte)(buffer >> bits & 0xFF));
            buffer &= (1 << bits) - 1;
        }

        return buffer == 0 ? [.. result] : null; // leftover padding bits must be zero
    }

    private static int Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1;
            crc &= 0xFFFF;
        }

        return crc;
    }

    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>Decodes a bech32 / bech32m string (BIP-173/350) and verifies its checksum. <c>data</c> excludes the checksum.</summary>
    private static bool TryDecodeBech32(string value, out string hrp, out byte[] data, out bool isBech32m, int maxLength = 90)
    {
        hrp = "";
        data = [];
        isBech32m = false;
        if (value.Length > maxLength || value.Any(c => c is < '!' or > '~')) return false;
        if (value.Any(char.IsUpper) && value.Any(char.IsLower)) return false;

        value = value.ToLowerInvariant();
        var separator = value.LastIndexOf('1');
        if (separator < 1 || value.Length - separator - 1 < 6) return false;

        var values = new List<int>();
        foreach (var c in value[(separator + 1)..])
        {
            var digit = Bech32Charset.IndexOf(c);
            if (digit < 0) return false;
            values.Add(digit);
        }

        var checksum = 1u;
        foreach (var c in value[..separator])
            checksum = Bech32Polymod(checksum, c >> 5);
        checksum = Bech32Polymod(checksum, 0);
        foreach (var c in value[..separator])
            checksum = Bech32Polymod(checksum, c & 31);
        foreach (var v in values)
            checksum = Bech32Polymod(checksum, v);

        if (checksum == 1) isBech32m = false;
        else if (checksum == 0x2bc830a3) isBech32m = true;
        else return false;

        hrp = value[..separator];
        data = values.Take(values.Count - 6).Select(v => (byte)v).ToArray();
        return true;
    }

    private static uint Bech32Polymod(uint checksum, int value)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        var top = checksum >> 25;
        checksum = (checksum & 0x1ffffff) << 5 ^ (uint)value;
        for (var i = 0; i < 5; i++)
            if ((top >> i & 1) != 0) checksum ^= generator[i];
        return checksum;
    }

    /// <summary>Regroups bits (5 to 8) without padding; null when the leftover bits are not zero padding of fewer than 5 bits.</summary>
    private static byte[]? ConvertBits(ReadOnlySpan<byte> data, int from, int to)
    {
        var result = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var v in data)
        {
            buffer = buffer << from | v;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte)(buffer >> bits & (1 << to) - 1));
            }

            buffer &= (1 << bits) - 1;
        }

        return bits >= from || buffer != 0 ? null : [.. result];
    }

    private static byte[]? DecodeBase58(string value, string alphabet)
    {
        BigInteger number = 0;
        foreach (var c in value)
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0) return null;
            number = number * 58 + digit;
        }

        var leadingZeros = value.TakeWhile(c => c == alphabet[0]).Count();
        var body = number.IsZero ? [] : number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[leadingZeros + body.Length];
        body.CopyTo(result, leadingZeros);
        return result;
    }

    [GeneratedRegex("^([A-Za-z0-9]{3}|[0-9A-Fa-f]{40})$")]
    private static partial Regex CurrencyCode();

    [GeneratedRegex("^0x[0-9a-fA-F]{64}$")]
    private static partial Regex SuiAddressPattern();

    [GeneratedRegex("^0x[0-9a-fA-F]{1,64}::[A-Za-z_][A-Za-z0-9_]*::[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SuiCoinTypePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{56}(\.([0-9a-fA-F]{2}){0,32})?$")]
    private static partial Regex CardanoAssetPattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,12}$")]
    private static partial Regex StellarAssetCode();
}
