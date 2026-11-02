using System.Buffers.Binary;
using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core;

/// <summary>
/// Hides an encrypted <see cref="Payload"/> in the low bits of an image and gets it back. See docs/FORMAT.md.
/// </summary>
/// <remarks>
/// Layout inside the image:
/// <list type="number">
/// <item>A 16-byte random salt, 1 bit per slot, in 128 slots at fixed pseudo-random positions.</item>
/// <item>Argon2id(password, salt) → AES-256-GCM key and a key for the data-slot permutation.</item>
/// <item>In permuted data slots, <c>bitsPerChannel</c> bits per slot:
/// header = GCM(u32 body length) and body = GCM(serialized payload). No plaintext magic bytes anywhere.</item>
/// </list>
/// The key is unique per embedding (fresh salt), so the two GCM nonces can be fixed constants.
/// </remarks>
public sealed class StegoEngine
{
    public const int DefaultBitsPerChannel = 1;

    /// <summary>Header (4-byte length + tag) plus body tag.</summary>
    public const int ContainerOverhead = HeaderSize + TagSize;

    internal const int SaltSize = 16;
    internal const int TagSize = 16;
    internal const int HeaderPlainSize = sizeof(uint);
    internal const int HeaderSize = HeaderPlainSize + TagSize;

    private static readonly byte[] HeaderNonce = Nonce(1);
    private static readonly byte[] BodyNonce = Nonce(2);

    private readonly KdfParameters _kdf;

    public StegoEngine(KdfParameters? kdfParameters = null)
    {
        _kdf = kdfParameters ?? KdfParameters.Default;
        _kdf.Validate();
    }

    /// <summary>Bits per channel values the engine can write; reveal tries each of them.</summary>
    public static IReadOnlyList<int> SupportedBitsPerChannel { get; } = [1, 2, 3, 4];

    public static CapacityInfo GetCapacity(PixelBuffer image, int bitsPerChannel = DefaultBitsPerChannel)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateBits(bitsPerChannel);
        var carrier = new RgbLsbCarrier(image);
        var layout = new SlotLayout(carrier);
        return new CapacityInfo(
            image.Width,
            image.Height,
            carrier.UsablePixels,
            bitsPerChannel,
            layout.DataSlotCount * bitsPerChannel / 8);
    }

    /// <summary>Bytes the payload will occupy in the image (after compression, with encryption overhead).</summary>
    public static long GetContainerSize(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ContainerOverhead + PayloadSerializer.Serialize(payload).LongLength;
    }

    /// <summary>Returns a new image carrying <paramref name="payload"/>. The cover is not modified.</summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer Embed(PixelBuffer cover, Payload payload, ReadOnlySpan<char> password, int bitsPerChannel = DefaultBitsPerChannel)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(payload);
        ValidateBits(bitsPerChannel);
        RequirePassword(password);

        var layout = new SlotLayout(new RgbLsbCarrier(cover));
        var body = PayloadSerializer.Serialize(payload);
        var containerSize = (long)ContainerOverhead + body.Length;
        var available = layout.DataSlotCount * bitsPerChannel / 8;
        if (containerSize > available)
        {
            CryptographicOperations.ZeroMemory(body);
            throw new StegoException(
                StegoError.InsufficientCapacity,
                $"Needs {containerSize:N0} bytes but the image holds {available:N0} at {bitsPerChannel} bit(s) per channel.");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var container = new byte[containerSize];
        try
        {
            using (var keys = KeyDerivation.Derive(password, salt, _kdf))
            {
                using var aes = new AesGcm(keys.AeadKey, TagSize);

                Span<byte> lengthPlain = stackalloc byte[HeaderPlainSize];
                BinaryPrimitives.WriteUInt32LittleEndian(lengthPlain, (uint)body.Length);
                aes.Encrypt(
                    HeaderNonce,
                    lengthPlain,
                    container.AsSpan(0, HeaderPlainSize),
                    container.AsSpan(HeaderPlainSize, TagSize),
                    AssociatedData("header", bitsPerChannel));

                aes.Encrypt(
                    BodyNonce,
                    body,
                    container.AsSpan(HeaderSize, body.Length),
                    container.AsSpan(HeaderSize + body.Length, TagSize),
                    AssociatedData("body", bitsPerChannel));

                var stego = cover.Clone();
                var carrier = new RgbLsbCarrier(stego);
                var random = new RandomBits();
                SlotWriter.Write(carrier, layout.SaltSource(), salt, 1, random);
                SlotWriter.Write(carrier, layout.DataSource(keys.PermutationKey), container, bitsPerChannel, random);
                return stego;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <exception cref="StegoException">
    /// <see cref="StegoError.NotFoundOrWrongPassword"/> or <see cref="StegoError.Corrupted"/>.
    /// </exception>
    public Payload Extract(PixelBuffer image, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequirePassword(password);

        var carrier = new RgbLsbCarrier(image);
        var layout = new SlotLayout(carrier);
        if (!layout.HasRoom)
        {
            throw NotFound();
        }

        var salt = new byte[SaltSize];
        new SlotReader(carrier, layout.SaltSource(), 1).ReadBytes(salt);

        using var keys = KeyDerivation.Derive(password, salt, _kdf);
        using var aes = new AesGcm(keys.AeadKey, TagSize);

        Span<byte> header = stackalloc byte[HeaderSize];
        Span<byte> lengthPlain = stackalloc byte[HeaderPlainSize];
        foreach (var bits in SupportedBitsPerChannel)
        {
            var available = layout.DataSlotCount * bits / 8;
            if (available < ContainerOverhead)
            {
                continue;
            }

            var reader = new SlotReader(carrier, layout.DataSource(keys.PermutationKey), bits);
            reader.ReadBytes(header);
            try
            {
                aes.Decrypt(HeaderNonce, header[..HeaderPlainSize], header[HeaderPlainSize..], lengthPlain, AssociatedData("header", bits));
            }
            catch (AuthenticationTagMismatchException)
            {
                continue; // Wrong password, or written with a different bits-per-channel.
            }

            // From here on the password is known to be right, so failures mean damage, not "not found".
            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthPlain);
            if (bodyLength > available - ContainerOverhead)
            {
                throw Corrupted("The stored length does not fit in this image.");
            }

            var sealedBody = new byte[bodyLength + TagSize];
            reader.ReadBytes(sealedBody);
            var body = new byte[bodyLength];
            try
            {
                aes.Decrypt(BodyNonce, sealedBody.AsSpan(0, (int)bodyLength), sealedBody.AsSpan((int)bodyLength), body, AssociatedData("body", bits));
                return PayloadSerializer.Deserialize(body);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw Corrupted("The hidden data was modified (authentication failed).", ex);
            }
            catch (InvalidDataException ex)
            {
                throw Corrupted(ex.Message, ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }

        throw NotFound();
    }

    private static StegoException NotFound() =>
        new(StegoError.NotFoundOrWrongPassword, "No hidden data found, or the password is wrong.");

    private static StegoException Corrupted(string detail, Exception? inner = null) =>
        new(StegoError.Corrupted, $"Hidden data is damaged: {detail}", inner);

    private static byte[] AssociatedData(string part, int bitsPerChannel) =>
        [.. "sweethide/v1/"u8, .. System.Text.Encoding.ASCII.GetBytes(part), (byte)bitsPerChannel];

    private static byte[] Nonce(byte counter)
    {
        var nonce = new byte[12];
        nonce[^1] = counter;
        return nonce;
    }

    private static void ValidateBits(int bitsPerChannel)
    {
        if (!SupportedBitsPerChannel.Contains(bitsPerChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerChannel), bitsPerChannel, "Bits per channel must be 1 to 4.");
        }
    }

    private static void RequirePassword(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("A password is required.", nameof(password));
        }
    }
}
