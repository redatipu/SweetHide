using System.Buffers.Binary;
using System.Security.Cryptography;

using SweetHide.Core.Crypto;

namespace SweetHide.Core.Stego;

/// <summary>A format v1 header that authenticated: the password is right and the bit depth is known.</summary>
internal sealed class OpenedV1(int bitsPerSlot, uint bodyLength, SlotReader reader, DerivedKeys keys) : IDisposable
{
    public int BitsPerSlot { get; } = bitsPerSlot;

    public uint BodyLength { get; } = bodyLength;

    public SlotReader Reader { get; } = reader;

    public DerivedKeys Keys { get; } = keys;

    public void Dispose() => Keys.Dispose();
}

/// <summary>
/// Reads images written by v0.1.0 (format v1). New images are always written in v2; the v1 writer survives only in
/// the tests, to keep this reader honest.
/// </summary>
/// <remarks>
/// Layout: a 16-byte salt in 128 public slots; data slots in the order of a password-keyed permutation of all slots,
/// skipping the salt; <c>[GCM(u32 length) | tag | GCM(payload) | tag]</c> at b bits per slot, with b (1-4) bound into
/// the associated data and found by trial.
/// </remarks>
internal static class V1Format
{
    public const int SaltSize = 16;
    public const int TagSize = 16;
    public const int HeaderPlainSize = sizeof(uint);
    public const int HeaderSize = HeaderPlainSize + TagSize;
    public const int Overhead = HeaderSize + TagSize;

    internal static readonly byte[] HeaderNonce = Nonce(1);
    internal static readonly byte[] BodyNonce = Nonce(2);

    public static byte[] ReadSalt(IStegoCarrier carrier, SlotLayout layout)
    {
        var salt = new byte[SaltSize];
        new SlotReader(carrier, layout.SaltSource(), 1).ReadBytes(salt);
        return salt;
    }

    /// <summary>Tries b = 1..4 with keys from <paramref name="master"/>.</summary>
    /// <returns>The opened container, or null when no header authenticates.</returns>
    /// <exception cref="StegoException">The header authenticated but its length cannot fit (damage).</exception>
    public static OpenedV1? TryOpen(IStegoCarrier carrier, SlotLayout layout, ReadOnlySpan<byte> master)
    {
        if (!layout.HasRoom)
        {
            return null;
        }

        var keys = KeyDerivation.V1Keys(master);
        try
        {
            using var aes = new AesGcm(keys.AeadKey, TagSize);
            Span<byte> header = stackalloc byte[HeaderSize];
            Span<byte> lengthPlain = stackalloc byte[HeaderPlainSize];
            foreach (var bits in StegoEngine.SupportedBitsPerChannel)
            {
                var available = layout.DataSlotCount * bits / 8;
                if (available < Overhead)
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
                    continue; // wrong password, or written with a different bits-per-channel
                }

                // From here on the password is known to be right, so failures mean damage, not "not found".
                var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthPlain);
                if (bodyLength > available - Overhead)
                {
                    throw new StegoException(StegoError.Corrupted, "Hidden data is damaged: the stored length does not fit in this image.");
                }

                var opened = new OpenedV1(bits, bodyLength, reader, keys);
                keys = null;
                return opened;
            }

            return null;
        }
        finally
        {
            keys?.Dispose();
        }
    }

    /// <summary>Reads and decrypts the body (the serialized payload).</summary>
    public static byte[] ReadBody(OpenedV1 opened)
    {
        var length = (int)opened.BodyLength;
        var sealedBody = new byte[length + TagSize];
        opened.Reader.ReadBytes(sealedBody);
        var body = new byte[length];
        try
        {
            using var aes = new AesGcm(opened.Keys.AeadKey, TagSize);
            aes.Decrypt(BodyNonce, sealedBody.AsSpan(0, length), sealedBody.AsSpan(length), body, AssociatedData("body", opened.BitsPerSlot));
            return body;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(body);
            throw new StegoException(StegoError.Corrupted, "Hidden data is damaged: the hidden data was modified (authentication failed).", ex);
        }
    }

    internal static byte[] AssociatedData(string part, int bitsPerSlot) =>
        [.. "sweethide/v1/"u8, .. System.Text.Encoding.ASCII.GetBytes(part), (byte)bitsPerSlot];

    private static byte[] Nonce(byte counter)
    {
        var nonce = new byte[12];
        nonce[^1] = counter;
        return nonce;
    }
}
