using System.Security.Cryptography;

namespace SweetHide.Core.Crypto;

/// <summary>
/// An optional second factor: the bytes of any file (a photo, a song, random data on a USB stick) mixed into key
/// derivation, so that revealing needs both the password and an identical copy of the file.
/// </summary>
/// <remarks>
/// Only a 32-byte digest is kept: <c>HMAC-SHA256(key = "sweethide/v2/key-file", data = file bytes)</c>. Any change to
/// the file, even one byte, makes it a different key file. The file is not secret to the format, so it should be one
/// an attacker cannot guess (not a well-known download).
/// </remarks>
public sealed class KeyFile
{
    private static readonly byte[] HmacKey = "sweethide/v2/key-file"u8.ToArray();

    private readonly byte[] _digest;

    private KeyFile(byte[] digest) => _digest = digest;

    internal ReadOnlySpan<byte> Digest => _digest;

    /// <exception cref="ArgumentException">The content is empty.</exception>
    public static KeyFile FromBytes(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            throw new ArgumentException("A key file must not be empty.", nameof(content));
        }

        return new KeyFile(HMACSHA256.HashData(HmacKey, content));
    }

    /// <summary>Hashes a stream from its current position to the end, without holding it in memory.</summary>
    /// <exception cref="ArgumentException">The stream is empty.</exception>
    public static KeyFile FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, HmacKey);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            hmac.AppendData(buffer, 0, read);
            total += read;
        }

        if (total == 0)
        {
            throw new ArgumentException("A key file must not be empty.", nameof(stream));
        }

        return new KeyFile(hmac.GetHashAndReset());
    }

    /// <exception cref="ArgumentException">The file is empty.</exception>
    public static KeyFile FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return FromStream(stream);
    }
}
