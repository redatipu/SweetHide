using System.Security.Cryptography;

using NSec.Cryptography;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Public-key mode, age style: a random 32-byte file key encrypts the container, and the recipient table holds one
/// copy of it per recipient, each wrapped with a key only that recipient can compute. Nothing names the recipients;
/// a reader finds its entry by trial decryption.
/// </summary>
/// <remarks>
/// One ephemeral X25519 key per image (shared by all entries, so only one curve point is stored):
/// <code>
/// shared  = X25519(ephemeral private, recipient public)
/// wrapKey = HKDF-SHA256(IKM = shared, salt = ephemeral public ‖ recipient public, info = "sweethide/v2/x25519", 32)
/// entry   = AES-256-GCM(wrapKey, nonce = 0, aad = "sweethide/v2/stanza", plaintext = fileKey ‖ u8 recipientCount)
/// </code>
/// </remarks>
internal static class RecipientStanza
{
    public const int MaxRecipients = 16;
    public const int FileKeySize = 32;
    public const int PlainSize = FileKeySize + 1;
    public const int Size = PlainSize + 16;
    public const int Slots = Size * 8;

    private static readonly byte[] Info = "sweethide/v2/x25519"u8.ToArray();
    private static readonly byte[] Aad = "sweethide/v2/stanza"u8.ToArray();
    private static readonly byte[] Nonce = new byte[12];

    public static byte[] Wrap(Key ephemeral, ReadOnlySpan<byte> ephemeralPublic, Recipient recipient, ReadOnlySpan<byte> fileKey, int recipientCount)
    {
        using var shared = KeyAgreementAlgorithm.X25519.Agree(ephemeral, recipient.ToPublicKey())
            ?? throw new ArgumentException("The public key cannot be used for key agreement.", nameof(recipient));
        var wrapKey = WrapKey(shared, ephemeralPublic, recipient);
        Span<byte> plain = stackalloc byte[PlainSize];
        try
        {
            fileKey.CopyTo(plain);
            plain[FileKeySize] = (byte)recipientCount;
            var stanza = new byte[Size];
            using var aes = new AesGcm(wrapKey, 16);
            aes.Encrypt(Nonce, plain, stanza.AsSpan(0, PlainSize), stanza.AsSpan(PlainSize), Aad);
            return stanza;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    /// <summary>The key that unwraps this identity's entry, or null when the stored point is unusable.</summary>
    /// <param name="identity">The reader's identity.</param>
    /// <param name="ephemeralPublic">The stored ephemeral public key, with the random top bit already cleared.</param>
    public static byte[]? UnwrapKey(Identity identity, ReadOnlySpan<byte> ephemeralPublic)
    {
        var publicKey = PublicKey.Import(KeyAgreementAlgorithm.X25519, ephemeralPublic, KeyBlobFormat.RawPublicKey);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(identity.Key, publicKey);
        return shared is null ? null : WrapKey(shared, ephemeralPublic, identity.Recipient);
    }

    /// <summary>Trial-decrypts one table entry.</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> unwrapKey, ReadOnlySpan<byte> stanza, Span<byte> fileKey, out int recipientCount)
    {
        Span<byte> plain = stackalloc byte[PlainSize];
        try
        {
            using var aes = new AesGcm(unwrapKey, 16);
            aes.Decrypt(Nonce, stanza[..PlainSize], stanza[PlainSize..], plain, Aad);
            plain[..FileKeySize].CopyTo(fileKey);
            recipientCount = plain[FileKeySize];
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            recipientCount = 0;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] WrapKey(SharedSecret shared, ReadOnlySpan<byte> ephemeralPublic, Recipient recipient) =>
        KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, [.. ephemeralPublic, .. recipient.Key], Info, 32);
}
