using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using NSec.Cryptography;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Your X25519 private key, which reveals images hidden for your <see cref="Recipient"/>. Keep it in a file encrypted
/// with a passphrase (<see cref="Export"/> / <see cref="Import"/>) and share only <see cref="Recipient"/>.
/// </summary>
/// <remarks>
/// Key file text:
/// <code>
/// -----BEGIN SWEETHIDE PRIVATE KEY-----
/// Public-Key: sweethide1…
/// base64 of: "SHKY" | u8 version (1) | u32 memoryKiB | u32 iterations | u32 parallelism | salt (16)
///            | public key (32) | AES-256-GCM(private key) (32) | tag (16)
/// -----END SWEETHIDE PRIVATE KEY-----
/// </code>
/// The AES key is HKDF-SHA256-Expand(Argon2id(passphrase, salt), "sweethide/identity/v1"); the nonce is zero (fresh salt
/// per export) and everything before the ciphertext is associated data. The Public-Key line is informational.
/// </remarks>
public sealed class Identity : IDisposable
{
    private const string BeginLine = "-----BEGIN SWEETHIDE PRIVATE KEY-----";
    private const string EndLine = "-----END SWEETHIDE PRIVATE KEY-----";
    private const string PublicKeyLabel = "Public-Key: ";
    private const byte FormatVersion = 1;
    private const int SaltSize = 16;
    private const int TagSize = 16;
    private const int HeaderSize = 4 + 1 + 12 + SaltSize + Recipient.KeySize;
    private const int BlobSize = HeaderSize + 32 + TagSize;

    // Upper bounds for Argon2id settings read from a file, so a crafted file cannot demand gigabytes or hours.
    private const int MaxMemoryKiB = 4 * 1024 * 1024;
    private const int MaxIterations = 64;
    private const int MaxParallelism = 64;

    private static readonly byte[] Magic = "SHKY"u8.ToArray();
    private static readonly byte[] KeyInfo = "sweethide/identity/v1"u8.ToArray();

    private Identity(Key key)
    {
        Key = key;
        Recipient = new Recipient(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>The public key to give to people who want to hide data for you.</summary>
    public Recipient Recipient { get; }

    internal Key Key { get; }

    public static Identity Generate() => new(Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));

    /// <summary>Encrypts the private key with a passphrase, as text that is safe to store in a file.</summary>
    /// <param name="passphrase">Protects the file; use a strong one.</param>
    /// <param name="kdf">Argon2id settings, stored in the file. Default <see cref="KdfParameters.Default"/>.</param>
    public string Export(ReadOnlySpan<char> passphrase, KdfParameters? kdf = null)
    {
        kdf ??= KdfParameters.Default;
        kdf.Validate();
        RequirePassphrase(passphrase);

        var blob = new byte[BlobSize];
        Magic.CopyTo(blob, 0);
        blob[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(5), kdf.MemoryKiB);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(9), kdf.Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(13), kdf.Parallelism);
        RandomNumberGenerator.Fill(blob.AsSpan(17, SaltSize));
        Recipient.Key.CopyTo(blob.AsSpan(17 + SaltSize));

        var privateKey = Key.Export(KeyBlobFormat.RawPrivateKey);
        var aesKey = DeriveFileKey(passphrase, blob.AsSpan(17, SaltSize), kdf);
        try
        {
            using var aes = new AesGcm(aesKey, TagSize);
            aes.Encrypt(new byte[12], privateKey, blob.AsSpan(HeaderSize, 32), blob.AsSpan(HeaderSize + 32), blob.AsSpan(0, HeaderSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(aesKey);
        }

        var text = new StringBuilder();
        text.Append(BeginLine).Append('\n');
        text.Append(PublicKeyLabel).Append(Recipient).Append('\n');
        var base64 = Convert.ToBase64String(blob);
        for (var i = 0; i < base64.Length; i += 64)
        {
            text.Append(base64.AsSpan(i, Math.Min(64, base64.Length - i))).Append('\n');
        }

        return text.Append(EndLine).Append('\n').ToString();
    }

    /// <summary>Decrypts a key file written by <see cref="Export"/>.</summary>
    /// <exception cref="FormatException">The text is not a SweetHide private key file.</exception>
    /// <exception cref="CryptographicException">Wrong passphrase, or the file was modified.</exception>
    public static Identity Import(string text, ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequirePassphrase(passphrase);
        var blob = ParseBlob(text);

        var kdf = new KdfParameters(
            BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(5)),
            BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(9)),
            BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(13)));
        if (kdf.Parallelism is < 1 or > MaxParallelism || kdf.Iterations is < 1 or > MaxIterations
            || kdf.MemoryKiB < 8 * kdf.Parallelism || kdf.MemoryKiB > MaxMemoryKiB)
        {
            throw new FormatException("The key file has unsupported Argon2id settings.");
        }

        var aesKey = DeriveFileKey(passphrase, blob.AsSpan(17, SaltSize), kdf);
        var privateKey = new byte[32];
        try
        {
            using (var aes = new AesGcm(aesKey, TagSize))
            {
                aes.Decrypt(new byte[12], blob.AsSpan(HeaderSize, 32), blob.AsSpan(HeaderSize + 32), privateKey, blob.AsSpan(0, HeaderSize));
            }

            var identity = new Identity(Key.Import(KeyAgreementAlgorithm.X25519, privateKey, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));
            if (!identity.Recipient.Key.SequenceEqual(blob.AsSpan(17 + SaltSize, Recipient.KeySize)))
            {
                identity.Dispose();
                throw new CryptographicException("The key file is damaged: its public key does not match the private key.");
            }

            return identity;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException("Wrong passphrase, or the key file was modified.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    public void Dispose() => Key.Dispose();

    private static byte[] ParseBlob(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var begin = lines.IndexOf(BeginLine);
        var end = lines.IndexOf(EndLine);
        if (begin < 0 || end <= begin)
        {
            throw new FormatException("Not a SweetHide private key file.");
        }

        var base64 = string.Concat(lines.Skip(begin + 1).Take(end - begin - 1).Where(line => !line.StartsWith(PublicKeyLabel, StringComparison.Ordinal)));
        var blob = new byte[BlobSize];
        if (!Convert.TryFromBase64String(base64, blob, out var written) || written != BlobSize
            || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new FormatException("Not a SweetHide private key file.");
        }

        if (blob[4] != FormatVersion)
        {
            throw new FormatException("The key file was written by a newer version of SweetHide.");
        }

        return blob;
    }

    private static byte[] DeriveFileKey(ReadOnlySpan<char> passphrase, ReadOnlySpan<byte> salt, KdfParameters kdf)
    {
        var master = KeyDerivation.Argon2Master(passphrase, salt, kdf);
        try
        {
            var key = new byte[32];
            HKDF.Expand(HashAlgorithmName.SHA256, master, key, KeyInfo);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static void RequirePassphrase(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.IsEmpty)
        {
            throw new ArgumentException("A passphrase is required.", nameof(passphrase));
        }
    }
}
