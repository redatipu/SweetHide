using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core;

/// <summary>
/// One share of a payload split across several images with <see cref="StegoEngine.EmbedShares"/>. Collect at least
/// <see cref="Threshold"/> shares of the same split and pass them to <see cref="StegoEngine.CombineShares"/>.
/// </summary>
/// <remarks>
/// Encoding (the content of a share container):
/// <code>
/// u8 version (1) | u8 threshold k | u8 share count n | u8 index x | split id (16) | tag (32) | y
/// </code>
/// The shared secret is <c>macKey (32) ‖ serialized payload</c> and <c>y</c> is this share of it. The tag is
/// <c>HMAC-SHA256(macKey, "sweethide/v2/shamir" ‖ split id ‖ k ‖ n ‖ payload)</c>: with fewer than k shares the MAC
/// key is unknown, so the tag reveals nothing, and after combining it proves no share was altered.
/// </remarks>
public sealed class ShamirShare
{
    internal const int MacKeySize = 32;
    private const byte Version = 1;
    private const int SplitIdSize = 16;
    private const int TagSize = 32;
    private const int HeaderSize = 4 + SplitIdSize + TagSize;

    private static readonly byte[] TagLabel = "sweethide/v2/shamir"u8.ToArray();

    private readonly byte[] _splitId;
    private readonly byte[] _tag;
    private readonly byte[] _y;

    private ShamirShare(int threshold, int shareCount, int index, byte[] splitId, byte[] tag, byte[] y)
    {
        Threshold = threshold;
        ShareCount = shareCount;
        Index = index;
        _splitId = splitId;
        _tag = tag;
        _y = y;
    }

    /// <summary>How many shares are needed (k).</summary>
    public int Threshold { get; }

    /// <summary>How many shares were made (n).</summary>
    public int ShareCount { get; }

    /// <summary>This share's number, 1 to <see cref="ShareCount"/>.</summary>
    public int Index { get; }

    /// <summary>Identifies the split; shares only combine with shares of the same split.</summary>
    public string SplitId => Convert.ToHexString(_splitId);

    /// <summary>Bytes of the encoded share for a payload that serializes to <paramref name="payloadBytes"/> bytes.</summary>
    internal static long EncodedSize(long payloadBytes) => HeaderSize + MacKeySize + payloadBytes;

    /// <summary>Splits a serialized payload into <paramref name="count"/> encoded shares.</summary>
    internal static byte[][] Split(byte[] payloadBytes, int threshold, int count, IRandomSource random)
    {
        var splitId = new byte[SplitIdSize];
        random.Fill(splitId);
        var secret = new byte[MacKeySize + payloadBytes.Length];
        try
        {
            random.Fill(secret.AsSpan(0, MacKeySize));
            payloadBytes.CopyTo(secret, MacKeySize);
            var tag = Tag(secret.AsSpan(0, MacKeySize), splitId, threshold, count, payloadBytes);
            var ys = Shamir.Split(secret, threshold, count, random);
            var encoded = new byte[count][];
            for (var i = 0; i < count; i++)
            {
                encoded[i] = new ShamirShare(threshold, count, i + 1, splitId, tag, ys[i]).Encode();
                CryptographicOperations.ZeroMemory(ys[i]);
            }

            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <exception cref="StegoException">The share is malformed.</exception>
    internal static ShamirShare Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderSize + MacKeySize)
        {
            throw Corrupted("the share is truncated.");
        }

        if (encoded[0] != Version)
        {
            throw new StegoException(StegoError.UnsupportedFormat, "The share was written by a newer version of SweetHide.");
        }

        int threshold = encoded[1], count = encoded[2], index = encoded[3];
        if (threshold < 2 || count < threshold || index < 1 || index > count)
        {
            throw Corrupted("the share's numbers are inconsistent.");
        }

        return new ShamirShare(
            threshold,
            count,
            index,
            encoded.Slice(4, SplitIdSize).ToArray(),
            encoded.Slice(4 + SplitIdSize, TagSize).ToArray(),
            encoded[HeaderSize..].ToArray());
    }

    /// <exception cref="StegoException">
    /// <see cref="StegoError.NotEnoughShares"/>, <see cref="StegoError.SharesDoNotMatch"/> or
    /// <see cref="StegoError.Corrupted"/>.
    /// </exception>
    internal static Payload Combine(IEnumerable<ShamirShare> shares)
    {
        var list = shares.Select(s => s ?? throw new ArgumentException("Shares must not be null.", nameof(shares))).ToList();
        if (list.Count == 0)
        {
            throw new StegoException(StegoError.NotEnoughShares, "No shares were given.");
        }

        var first = list[0];
        if (list.Any(s => !s._splitId.AsSpan().SequenceEqual(first._splitId) || s.Threshold != first.Threshold
            || s.ShareCount != first.ShareCount || s._y.Length != first._y.Length || !s._tag.AsSpan().SequenceEqual(first._tag)))
        {
            throw new StegoException(StegoError.SharesDoNotMatch, "These shares belong to different splits.");
        }

        var byIndex = new Dictionary<int, ShamirShare>();
        foreach (var share in list)
        {
            if (byIndex.TryGetValue(share.Index, out var existing) && !existing._y.AsSpan().SequenceEqual(share._y))
            {
                throw new StegoException(StegoError.SharesDoNotMatch, $"Two different shares claim number {share.Index}.");
            }

            byIndex[share.Index] = share;
        }

        if (byIndex.Count < first.Threshold)
        {
            throw new StegoException(
                StegoError.NotEnoughShares,
                $"Needs {first.Threshold} of the {first.ShareCount} shares; only {byIndex.Count} given.");
        }

        var chosen = byIndex.Values.OrderBy(s => s.Index).Take(first.Threshold).Select(s => ((byte)s.Index, s._y)).ToList();
        var secret = Shamir.Combine(chosen);
        try
        {
            var payloadBytes = secret.AsSpan(MacKeySize);
            var tag = Tag(secret.AsSpan(0, MacKeySize), first._splitId, first.Threshold, first.ShareCount, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(tag, first._tag))
            {
                throw Corrupted("a share was altered (the combined payload does not authenticate).");
            }

            try
            {
                return PayloadSerializer.Deserialize(payloadBytes);
            }
            catch (InvalidDataException ex)
            {
                throw new StegoException(StegoError.Corrupted, $"Hidden data is damaged: {ex.Message}", ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private byte[] Encode()
    {
        var encoded = new byte[HeaderSize + _y.Length];
        encoded[0] = Version;
        encoded[1] = (byte)Threshold;
        encoded[2] = (byte)ShareCount;
        encoded[3] = (byte)Index;
        _splitId.CopyTo(encoded, 4);
        _tag.CopyTo(encoded, 4 + SplitIdSize);
        _y.CopyTo(encoded, HeaderSize);
        return encoded;
    }

    private static byte[] Tag(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> splitId, int threshold, int count, ReadOnlySpan<byte> payloadBytes)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        Span<byte> numbers = stackalloc byte[2];
        numbers[0] = (byte)threshold;
        numbers[1] = (byte)count;
        hmac.AppendData(TagLabel);
        hmac.AppendData(splitId);
        hmac.AppendData(numbers);
        hmac.AppendData(payloadBytes);
        return hmac.GetHashAndReset();
    }

    private static StegoException Corrupted(string detail) => new(StegoError.Corrupted, $"Hidden data is damaged: {detail}");
}
