using System.Buffers.Binary;
using System.Security.Cryptography;

using SweetHide.Core.Crypto;

namespace SweetHide.Core.Stego;

/// <summary>What a container's content is.</summary>
internal enum ContentKind : byte
{
    /// <summary>A serialized <see cref="Payloads.Payload"/>.</summary>
    Payload = 1,

    /// <summary>One share of a Shamir split.</summary>
    Share = 2,
}

/// <summary>The 11-byte plaintext of a format v2 header.</summary>
/// <remarks>
/// <code>
/// u8  flags          bits 0-1: bits per slot - 1; bit 2: adaptive; bits 3-7: reserved, 0
/// u8  threshold      adaptive texture threshold (0 when not adaptive)
/// u8  kind           1 = payload, 2 = Shamir share
/// u32 bodyLength     body plaintext bytes (LE); the body ciphertext adds a 16-byte tag
/// u32 contentLength  meaningful prefix of the body (LE); the rest is zero padding (deniable mode)
/// </code>
/// </remarks>
internal readonly record struct ContainerHeader(int BitsPerSlot, bool Adaptive, int Threshold, ContentKind Kind, int BodyLength, int ContentLength)
{
    public const int PlainSize = 11;
    public const int SealedSize = PlainSize + V2Container.TagSize;

    private const int AdaptiveFlag = 1 << 2;
    private const int ReservedFlags = ~0x07 & 0xFF;

    public void Encode(Span<byte> destination)
    {
        destination[0] = (byte)((BitsPerSlot - 1) | (Adaptive ? AdaptiveFlag : 0));
        destination[1] = (byte)Threshold;
        destination[2] = (byte)Kind;
        BinaryPrimitives.WriteInt32LittleEndian(destination[3..], BodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[7..], ContentLength);
    }

    /// <summary>Parses an authenticated header.</summary>
    /// <exception cref="StegoException">Unknown flags or kind (a newer version wrote it), or inconsistent lengths.</exception>
    public static ContainerHeader Decode(ReadOnlySpan<byte> source)
    {
        var flags = source[0];
        var kind = (ContentKind)source[2];
        if ((flags & ReservedFlags) != 0 || kind is not (ContentKind.Payload or ContentKind.Share))
        {
            throw new StegoException(
                StegoError.UnsupportedFormat,
                "The hidden data was written by a newer version of SweetHide. Update SweetHide to reveal it.");
        }

        var adaptive = (flags & AdaptiveFlag) != 0;
        var threshold = source[1];
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(source[3..]);
        var contentLength = BinaryPrimitives.ReadInt32LittleEndian(source[7..]);
        if (bodyLength < 0 || contentLength < 0 || contentLength > bodyLength || (!adaptive && threshold != 0))
        {
            throw new StegoException(StegoError.Corrupted, "Hidden data is damaged: the header is inconsistent.");
        }

        return new ContainerHeader((flags & 3) + 1, adaptive, threshold, kind, bodyLength, contentLength);
    }
}

/// <summary>
/// The elements of a sequence one container uses. The header takes the first 216 elements whose slot is not a
/// reference slot (headers must never disturb textures); the body takes every other element, in order.
/// </summary>
internal sealed class ContainerSlots
{
    public const int HeaderSlotCount = ContainerHeader.SealedSize * 8;

    // Reference slots passed over while collecting the header; a plain (non-adaptive) body uses them first.
    private readonly long[] _skipped;

    private ContainerSlots(SlotSequence sequence, long[] header, long[] skipped, long headerEnd)
    {
        Sequence = sequence;
        HeaderSlots = header;
        _skipped = skipped;
        HeaderEnd = headerEnd;
    }

    public SlotSequence Sequence { get; }

    public IReadOnlyList<long> HeaderSlots { get; }

    /// <summary>First element after the last header element.</summary>
    public long HeaderEnd { get; }

    /// <summary>Elements available to a plain body: everything except the header.</summary>
    public long PlainBodyLength => Sequence.Length - HeaderSlotCount;

    /// <returns>Null when the sequence holds fewer than 216 non-reference slots.</returns>
    public static ContainerSlots? Locate(IStegoCarrier carrier, SlotSequence sequence)
    {
        if (sequence.Length < HeaderSlotCount)
        {
            return null;
        }

        var adaptive = carrier as IAdaptiveCarrier;
        var header = new long[HeaderSlotCount];
        var skipped = new List<long>();
        long element = 0;
        var found = 0;
        while (found < HeaderSlotCount)
        {
            if (element >= sequence.Length)
            {
                return null;
            }

            var slot = sequence.SlotAt(element++);
            if (adaptive is null || adaptive.Texture(slot) >= 0)
            {
                header[found++] = slot;
            }
            else
            {
                skipped.Add(slot);
            }
        }

        return new ContainerSlots(sequence, header, [.. skipped], element);
    }

    /// <summary>Slot of a plain body's group <paramref name="group"/>.</summary>
    public long PlainBodySlot(long group) =>
        group < _skipped.Length ? _skipped[group] : Sequence.SlotAt(HeaderEnd + group - _skipped.Length);
}

/// <summary>A container whose header authenticated with some key.</summary>
internal sealed class OpenedContainer(ContainerPlacement placement, ContainerHeader header, ContainerSlots slots, byte[] aeadKey) : IDisposable
{
    public ContainerPlacement Placement { get; } = placement;

    public ContainerHeader Header { get; } = header;

    public ContainerSlots Slots { get; } = slots;

    public byte[] AeadKey { get; } = aeadKey;

    public void Dispose() => CryptographicOperations.ZeroMemory(AeadKey);
}

/// <summary>
/// Format v2 container: an AES-256-GCM header (27 bytes, 1 bit per slot) and body (bits per slot as the header says),
/// placed along a key-dependent <see cref="SlotSequence"/>. See docs/FORMAT.md.
/// </summary>
internal static class V2Container
{
    public const int TagSize = 16;

    /// <summary>Header (with tag) plus body tag.</summary>
    public const int Overhead = ContainerHeader.SealedSize + TagSize;

    /// <summary>Adaptive mode aims to fill at most this share of the slots it selects (see <see cref="PickThreshold"/>).</summary>
    public const double AdaptiveTargetLoad = 0.5;

    private static readonly byte[] HeaderNonce = Nonce(1);
    private static readonly byte[] BodyNonce = Nonce(2);

    /// <summary>Largest container (header, body and tags) that fits in <paramref name="bodySlots"/> body slots.</summary>
    public static long ContainerBytes(long bodySlots, int bits) =>
        bodySlots <= 0 ? 0 : ContainerHeader.SealedSize + (bodySlots * bits / 8);

    /// <summary>
    /// Writes one container. With <c>fill</c> the body is padded with zeros until the container fills its placement
    /// (deniable mode).
    /// </summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>; nothing has been written then.</exception>
    public static void Write(
        V2Layout layout,
        long dataStart,
        ContainerPlacement placement,
        ReadOnlySpan<byte> root,
        ContentKind kind,
        byte[] content,
        int bits,
        bool adaptive,
        bool fill,
        IRandomSource random)
    {
        var carrier = layout.Carrier;
        var adaptiveCarrier = carrier as IAdaptiveCarrier;
        if (adaptive && adaptiveCarrier is null)
        {
            throw new NotSupportedException("This carrier does not support adaptive embedding.");
        }

        using var keys = KeyDerivation.ContainerKeys(root);
        var sequence = layout.Sequence(dataStart, placement, keys.PermutationKey);
        var slots = ContainerSlots.Locate(carrier, sequence) ?? throw Capacity(content.Length, 0);

        var needed = GroupIo.GroupCount((long)content.Length + TagSize, bits);
        var threshold = 0;
        long available;
        if (adaptive)
        {
            // Textures are final here: only the public region and recipient table touch reference slots, and both
            // are written before any container.
            var histogram = GroupIo.TextureHistogram(adaptiveCarrier!, sequence, slots.HeaderEnd);
            (threshold, available) = PickThreshold(histogram, fill ? 0 : needed);
        }
        else
        {
            available = slots.PlainBodyLength;
        }

        if (needed > available)
        {
            throw Capacity(content.Length, (available * bits / 8) - TagSize);
        }

        var bodyLength = fill ? (int)Math.Min(int.MaxValue, (available * bits / 8) - TagSize) : content.Length;
        var header = new ContainerHeader(bits, adaptive, threshold, kind, bodyLength, content.Length);
        Span<byte> headerPlain = stackalloc byte[ContainerHeader.PlainSize];
        header.Encode(headerPlain);

        var sealedHeader = new byte[ContainerHeader.SealedSize];
        var sealedBody = new byte[(long)bodyLength + TagSize];
        var body = new byte[bodyLength];
        try
        {
            content.CopyTo(body, 0);
            using (var aes = new AesGcm(keys.AeadKey, TagSize))
            {
                aes.Encrypt(
                    HeaderNonce,
                    headerPlain,
                    sealedHeader.AsSpan(0, ContainerHeader.PlainSize),
                    sealedHeader.AsSpan(ContainerHeader.PlainSize),
                    HeaderAad(placement));
                aes.Encrypt(BodyNonce, body, sealedBody.AsSpan(0, bodyLength), sealedBody.AsSpan(bodyLength), BodyAad(placement, headerPlain));
            }

            var randomBits = new RandomBits(random);
            SlotWriter.Write(carrier, new ListSource(slots.HeaderSlots), sealedHeader, 1, randomBits);
            if (adaptive)
            {
                var accepted = GroupIo.WriteFiltered(
                    carrier, sequence, slots.HeaderEnd, slot => adaptiveCarrier!.Texture(slot) >= threshold, sealedBody, bits, random);
                if (!accepted)
                {
                    throw new InvalidOperationException("Adaptive capacity changed while writing.");
                }
            }
            else
            {
                GroupIo.Write(carrier, slots.PlainBodySlot, sealedBody, bits, random);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>Tries the header of every placement with one root key.</summary>
    /// <returns>The container, or null when no header authenticates (wrong key or no data).</returns>
    /// <exception cref="StegoException">A header authenticated but is invalid.</exception>
    public static OpenedContainer? TryOpen(V2Layout layout, long dataStart, ReadOnlySpan<byte> root)
    {
        using var keys = KeyDerivation.ContainerKeys(root);
        using var aes = new AesGcm(keys.AeadKey, TagSize);
        Span<byte> sealedHeader = stackalloc byte[ContainerHeader.SealedSize];
        Span<byte> headerPlain = stackalloc byte[ContainerHeader.PlainSize];
        foreach (var placement in (ReadOnlySpan<ContainerPlacement>)[ContainerPlacement.Whole, ContainerPlacement.FirstHalf, ContainerPlacement.SecondHalf])
        {
            var sequence = layout.Sequence(dataStart, placement, keys.PermutationKey);
            var slots = ContainerSlots.Locate(layout.Carrier, sequence);
            if (slots is null)
            {
                continue;
            }

            new SlotReader(layout.Carrier, new ListSource(slots.HeaderSlots), 1).ReadBytes(sealedHeader);
            try
            {
                aes.Decrypt(
                    HeaderNonce,
                    sealedHeader[..ContainerHeader.PlainSize],
                    sealedHeader[ContainerHeader.PlainSize..],
                    headerPlain,
                    HeaderAad(placement));
            }
            catch (AuthenticationTagMismatchException)
            {
                continue; // wrong key, or not this placement
            }

            return new OpenedContainer(placement, ContainerHeader.Decode(headerPlain), slots, (byte[])keys.AeadKey.Clone());
        }

        return null;
    }

    /// <summary>Reads and decrypts the body of an opened container.</summary>
    /// <returns>The content (without padding).</returns>
    /// <exception cref="StegoException"><see cref="StegoError.Corrupted"/>.</exception>
    public static byte[] ReadContent(IStegoCarrier carrier, OpenedContainer container)
    {
        var header = container.Header;
        var slots = container.Slots;
        var bits = header.BitsPerSlot;
        var adaptiveCarrier = carrier as IAdaptiveCarrier;
        if (bits > carrier.MaxBitsPerSlot || (header.Adaptive && adaptiveCarrier is null))
        {
            throw Corrupted("The header asks for an embedding this image type cannot hold.");
        }

        // Bounded by the image before allocating: a damaged length can never ask for more than the image holds.
        var sealedLength = (long)header.BodyLength + TagSize;
        if (GroupIo.GroupCount(sealedLength, bits) > slots.PlainBodyLength)
        {
            throw Corrupted("The stored length does not fit in this image.");
        }

        var sealedBody = new byte[sealedLength];
        if (header.Adaptive)
        {
            var threshold = header.Threshold;
            if (!GroupIo.ReadFiltered(carrier, slots.Sequence, slots.HeaderEnd, slot => adaptiveCarrier!.Texture(slot) >= threshold, sealedBody, bits))
            {
                throw Corrupted("The stored length does not fit in this image.");
            }
        }
        else
        {
            GroupIo.Read(carrier, slots.PlainBodySlot, sealedBody, bits);
        }

        Span<byte> headerPlain = stackalloc byte[ContainerHeader.PlainSize];
        header.Encode(headerPlain);
        var body = new byte[header.BodyLength];
        try
        {
            using var aes = new AesGcm(container.AeadKey, TagSize);
            aes.Decrypt(
                BodyNonce,
                sealedBody.AsSpan(0, header.BodyLength),
                sealedBody.AsSpan(header.BodyLength),
                body,
                BodyAad(container.Placement, headerPlain));
            return body.AsSpan(0, header.ContentLength).ToArray();
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw Corrupted("The hidden data was modified (authentication failed).", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>
    /// Picks the adaptive texture threshold for a body of <paramref name="needed"/> groups (0: fill everything).
    /// </summary>
    /// <remarks>
    /// Let <c>fit</c> be the highest threshold whose slots can hold the body at all and <c>half</c> the highest whose
    /// slots hold it at most <see cref="AdaptiveTargetLoad"/> full. The result is <c>max(half, ceil(fit / 2))</c>:
    /// spread out where the image has busy slots to spare, but never into slots less than half as busy as strictly
    /// necessary. Plain LSB matching changes about half the groups however many slots it is offered, so spreading only
    /// pays while it stays in busy regions.
    /// </remarks>
    internal static (int Threshold, long Available) PickThreshold(long[] histogram, long needed)
    {
        var cumulative = new long[histogram.Length + 1];
        for (var t = histogram.Length - 1; t >= 0; t--)
        {
            cumulative[t] = cumulative[t + 1] + histogram[t];
        }

        int fit = -1, half = -1;
        for (var t = histogram.Length - 1; t >= 0 && needed > 0; t--)
        {
            if (fit < 0 && cumulative[t] >= needed)
            {
                fit = t;
            }

            if (half < 0 && cumulative[t] * AdaptiveTargetLoad >= needed)
            {
                half = t;
            }
        }

        if (fit < 0)
        {
            return (0, cumulative[0]); // fill mode, or does not fit (the caller reports the capacity)
        }

        var threshold = Math.Max(half, (fit + 1) / 2);
        return (threshold, cumulative[threshold]);
    }

    private static byte[] HeaderAad(ContainerPlacement placement) => [.. "sweethide/v2/header"u8, (byte)placement];

    private static byte[] BodyAad(ContainerPlacement placement, ReadOnlySpan<byte> headerPlain) =>
        [.. "sweethide/v2/body"u8, (byte)placement, .. headerPlain];

    private static StegoException Capacity(long contentBytes, long availableBytes) => new(
        StegoError.InsufficientCapacity,
        $"Needs {contentBytes + Overhead:N0} bytes but the image holds {Math.Max(0, availableBytes) + Overhead:N0} with these settings.");

    private static StegoException Corrupted(string detail, Exception? inner = null) =>
        new(StegoError.Corrupted, $"Hidden data is damaged: {detail}", inner);

    private static byte[] Nonce(byte counter)
    {
        var nonce = new byte[12];
        nonce[^1] = counter;
        return nonce;
    }

    private sealed class ListSource(IReadOnlyList<long> slots) : ISlotSource
    {
        private int _next;

        public long NextSlot() => slots[_next++];
    }
}
