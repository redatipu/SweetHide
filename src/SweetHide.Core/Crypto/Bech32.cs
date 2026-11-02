using System.Text;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Bech32 (BIP-173): a base32 text encoding with a 6-character BCH checksum that catches any typo of up to four
/// characters. Used for public keys, as age does: <c>sweethide1…</c>.
/// </summary>
internal static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const int ChecksumLength = 6;
    private const int MaxLength = 90;

    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    public static string Encode(string hrp, ReadOnlySpan<byte> data)
    {
        var values = ConvertBits(data, 8, 5, pad: true)!;
        var checksum = Checksum(hrp, values);
        var builder = new StringBuilder(hrp.Length + 1 + values.Length + ChecksumLength);
        builder.Append(hrp).Append('1');
        foreach (var value in values.Concat(checksum))
        {
            builder.Append(Charset[value]);
        }

        return builder.ToString();
    }

    /// <summary>Decodes a string with the given human-readable part. Accepts all-lowercase or all-uppercase.</summary>
    public static bool TryDecode(string? text, string hrp, out byte[] data)
    {
        data = [];
        if (text is null || text.Length > MaxLength || text.Any(c => c < 33 || c > 126))
        {
            return false;
        }

        if (text.Any(char.IsLower) && text.Any(char.IsUpper))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        var separator = lower.LastIndexOf('1');
        if (separator < 1 || separator + 1 + ChecksumLength > lower.Length || lower[..separator] != hrp)
        {
            return false;
        }

        var values = new byte[lower.Length - separator - 1];
        for (var i = 0; i < values.Length; i++)
        {
            var index = Charset.IndexOf(lower[separator + 1 + i], StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            values[i] = (byte)index;
        }

        if (Polymod([.. ExpandHrp(hrp), .. values]) != 1)
        {
            return false;
        }

        var decoded = ConvertBits(values.AsSpan(0, values.Length - ChecksumLength), 5, 8, pad: false);
        if (decoded is null)
        {
            return false;
        }

        data = decoded;
        return true;
    }

    private static byte[] Checksum(string hrp, byte[] values)
    {
        var polymod = Polymod([.. ExpandHrp(hrp), .. values, 0, 0, 0, 0, 0, 0]) ^ 1;
        var checksum = new byte[ChecksumLength];
        for (var i = 0; i < ChecksumLength; i++)
        {
            checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 31);
        }

        return checksum;
    }

    private static uint Polymod(ReadOnlySpan<byte> values)
    {
        uint check = 1;
        foreach (var value in values)
        {
            var top = check >> 25;
            check = ((check & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < Generator.Length; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    check ^= Generator[i];
                }
            }
        }

        return check;
    }

    private static byte[] ExpandHrp(string hrp) => [.. hrp.Select(c => (byte)(c >> 5)), 0, .. hrp.Select(c => (byte)(c & 31))];

    /// <summary>Regroups bits; null when the input has non-zero padding or a leftover group of 5+ bits.</summary>
    private static byte[]? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var accumulator = 0;
        var bits = 0;
        var maxValue = (1 << toBits) - 1;
        var maxAccumulator = (1 << (fromBits + toBits - 1)) - 1;
        var result = new List<byte>((data.Length * fromBits / toBits) + 1);
        foreach (var value in data)
        {
            accumulator = ((accumulator << fromBits) | value) & maxAccumulator;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((accumulator >> bits) & maxValue));
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((accumulator << (toBits - bits)) & maxValue));
            }
        }
        else if (bits >= fromBits || ((accumulator << (toBits - bits)) & maxValue) != 0)
        {
            return null;
        }

        return [.. result];
    }
}
