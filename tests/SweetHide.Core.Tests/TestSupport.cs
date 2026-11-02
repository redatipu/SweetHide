using System.Buffers.Binary;
using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

/// <summary>Deterministic randomness (SHA-256 in counter mode) so a test can pin the exact image the writer produces.</summary>
internal sealed class SeededRandom(string seed) : IRandomSource
{
    private readonly byte[] _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
    private readonly byte[] _block = new byte[32];
    private int _available;
    private long _counter;

    public void Fill(Span<byte> destination)
    {
        Span<byte> input = stackalloc byte[40];
        for (var i = 0; i < destination.Length; i++)
        {
            if (_available == 0)
            {
                _key.CopyTo(input);
                BinaryPrimitives.WriteInt64LittleEndian(input[32..], _counter++);
                SHA256.HashData(input, _block);
                _available = _block.Length;
            }

            destination[i] = _block[^_available--];
        }
    }
}

/// <summary>The v0.1.0 writer (format v1), kept here so the v1 reader can be tested on any input.</summary>
internal static class V1Writer
{
    public static PixelBuffer Embed(PixelBuffer cover, Payload payload, string password, int bits, KdfParameters kdf)
    {
        var stego = cover.Clone();
        var carrier = new RgbLsbCarrier(stego);
        var layout = new SlotLayout(carrier);
        var body = PayloadSerializer.Serialize(payload);
        var container = new byte[V1Format.Overhead + body.Length];
        var salt = RandomNumberGenerator.GetBytes(V1Format.SaltSize);
        using var keys = KeyDerivation.Derive(password, salt, kdf);
        using var aes = new AesGcm(keys.AeadKey, V1Format.TagSize);
        Span<byte> lengthPlain = stackalloc byte[V1Format.HeaderPlainSize];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthPlain, (uint)body.Length);
        aes.Encrypt(
            V1Format.HeaderNonce,
            lengthPlain,
            container.AsSpan(0, V1Format.HeaderPlainSize),
            container.AsSpan(V1Format.HeaderPlainSize, V1Format.TagSize),
            V1Format.AssociatedData("header", bits));
        aes.Encrypt(
            V1Format.BodyNonce,
            body,
            container.AsSpan(V1Format.HeaderSize, body.Length),
            container.AsSpan(V1Format.HeaderSize + body.Length, V1Format.TagSize),
            V1Format.AssociatedData("body", bits));

        var random = new RandomBits();
        SlotWriter.Write(carrier, layout.SaltSource(), salt, 1, random);
        SlotWriter.Write(carrier, layout.DataSource(keys.PermutationKey), container, bits, random);
        return stego;
    }
}
