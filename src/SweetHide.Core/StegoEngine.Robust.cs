using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Robust;

namespace SweetHide.Core;

/// <summary>What <see cref="StegoEngine.RevealRobust"/> found.</summary>
/// <param name="Status">Found, damaged beyond repair, or nothing for this password.</param>
/// <param name="Payload">The revealed payload when <paramref name="Status"/> is <see cref="RobustStatus.Found"/>.</param>
/// <param name="Profile">The chat-app profile the image was made for, when its sync pattern matched.</param>
/// <param name="SyncScore">About 0 for images without robust data for this password, up to 1 for untouched ones.</param>
/// <param name="RawBitErrorRate">How much the app's recompression disturbed the hidden bits, before error correction.</param>
/// <param name="CorrectedSymbols">Bytes repaired by error correction.</param>
public sealed record RobustRevealResult(
    RobustStatus Status,
    Payload? Payload,
    RobustProfile? Profile,
    double SyncScore,
    double RawBitErrorRate,
    int CorrectedSymbols);

/// <summary>
/// Password layer for robust mode (<see cref="RobustStego"/>): the payload is serialized like any other, sealed with
/// AES-256-GCM, and the layout of the hidden bits is keyed by the password too.
/// </summary>
/// <remarks>
/// <para>
/// A reader must know where the bits are before it can read anything, so unlike the LSB formats there is no room
/// for a random salt in front of the key derivation: the layout key comes from
/// <c>Argon2id(password, fixed robust salt)</c> with the <see cref="KdfProfile.Normal"/> profile, plus the key file
/// when one is used. That lets an attacker precompute guesses once for every robust image, which is the price of
/// surviving recompression; a strong passphrase matters even more here.
/// </para>
/// <para>
/// The bytes handed to <see cref="RobustStego"/> are <c>salt(16) ‖ AES-GCM(serialized payload) ‖ tag(16)</c>. The
/// AEAD key is <c>HKDF(salt = that random salt, IKM = password root)</c>, unique per image, so the nonce is fixed.
/// </para>
/// </remarks>
public sealed partial class StegoEngine
{
    /// <summary>Bytes robust mode adds around the serialized payload (salt and authentication tag).</summary>
    public const int RobustOverhead = RobustSaltSize + RobustTagSize;

    private const int RobustSaltSize = 16;
    private const int RobustTagSize = 16;

    private static readonly byte[] RobustArgonSalt = SHA256.HashData("sweethide/robust/v1/argon2-salt"u8)[..16];
    private static readonly byte[] RobustLayoutInfo = "sweethide/robust/v1/layout-key"u8.ToArray();
    private static readonly byte[] RobustAeadInfo = "sweethide/robust/v1/aead-key"u8.ToArray();
    private static readonly byte[] RobustAssociatedData = "sweethide/robust/v1/body"u8.ToArray();
    private static readonly byte[] RobustNonce = new byte[12];

    /// <summary>
    /// Bytes of serialized payload (<see cref="GetRobustPayloadSize"/>) a cover of this size can carry in robust mode
    /// for <paramref name="profile"/>; 0 when it is too small.
    /// </summary>
    public static int GetRobustCapacity(int width, int height, RobustProfile profile) =>
        Math.Max(0, RobustStego.MaxPayloadBytes(width, height, profile) - RobustOverhead);

    /// <summary>Bytes <paramref name="payload"/> takes in robust mode, before <see cref="RobustOverhead"/>.</summary>
    public static int GetRobustPayloadSize(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var body = PayloadSerializer.Serialize(payload);
        CryptographicOperations.ZeroMemory(body);
        return body.Length;
    }

    /// <summary>
    /// Hides a short payload so it survives being sent as a photo through the chat app <paramref name="profile"/> is
    /// made for. The result is already shrunk to the profile's size; save it with
    /// <see cref="RobustStego.WriteJpeg"/>. Capacity is a few hundred bytes at most (<see cref="GetRobustCapacity"/>).
    /// </summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer EmbedRobust(PixelBuffer cover, Payload payload, ReadOnlySpan<char> password, RobustProfile profile, KeyFile? keyFile = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(profile);
        RequirePassword(password);

        var body = PayloadSerializer.Serialize(payload);
        var sealedBytes = new byte[RobustOverhead + body.Length];
        try
        {
            var capacity = GetRobustCapacity(cover.Width, cover.Height, profile);
            if (body.Length > capacity)
            {
                throw new StegoException(
                    StegoError.InsufficientCapacity,
                    $"Needs {body.Length:N0} bytes but this cover holds {capacity:N0} in {profile.Name} robust mode.");
            }

            var salt = sealedBytes.AsSpan(0, RobustSaltSize);
            _random.Fill(salt);
            var (layoutKey, root) = RobustKeys(password, keyFile);
            try
            {
                using (var aes = new AesGcm(RobustAeadKey(root, salt), RobustTagSize))
                {
                    aes.Encrypt(
                        RobustNonce,
                        body,
                        sealedBytes.AsSpan(RobustSaltSize, body.Length),
                        sealedBytes.AsSpan(RobustSaltSize + body.Length, RobustTagSize),
                        RobustAssociatedData);
                }

                return RobustStego.Embed(cover, sealedBytes, layoutKey, profile);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(layoutKey);
                CryptographicOperations.ZeroMemory(root);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>
    /// Looks for robust-mode data for this password, trying every built-in profile (also on images the app shrank a
    /// little further). A wrong password and "nothing hidden" both give <see cref="RobustStatus.NotFound"/>.
    /// </summary>
    /// <exception cref="StegoException"><see cref="StegoError.Corrupted"/> when the data decodes but fails authentication.</exception>
    public RobustRevealResult RevealRobust(PixelBuffer image, ReadOnlySpan<char> password, KeyFile? keyFile = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequirePassword(password);

        var (layoutKey, root) = RobustKeys(password, keyFile);
        try
        {
            var found = RobustStego.Extract(image, layoutKey);
            if (found.Status != RobustStatus.Found)
            {
                return new RobustRevealResult(found.Status, null, found.Profile, found.SyncScore, found.RawBitErrorRate, found.CorrectedSymbols);
            }

            var sealedBytes = found.Payload.Span;
            if (sealedBytes.Length < RobustOverhead)
            {
                throw new StegoException(StegoError.Corrupted, "Hidden data is damaged: it is too short.");
            }

            var bodyLength = sealedBytes.Length - RobustOverhead;
            var body = new byte[bodyLength];
            try
            {
                using (var aes = new AesGcm(RobustAeadKey(root, sealedBytes[..RobustSaltSize]), RobustTagSize))
                {
                    aes.Decrypt(
                        RobustNonce,
                        sealedBytes.Slice(RobustSaltSize, bodyLength),
                        sealedBytes.Slice(RobustSaltSize + bodyLength, RobustTagSize),
                        body,
                        RobustAssociatedData);
                }

                return new RobustRevealResult(RobustStatus.Found, Deserialize(body), found.Profile, found.SyncScore, found.RawBitErrorRate, found.CorrectedSymbols);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new StegoException(StegoError.Corrupted, "Hidden data is damaged (authentication failed).", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(layoutKey);
            CryptographicOperations.ZeroMemory(root);
        }
    }

    private (byte[] LayoutKey, byte[] Root) RobustKeys(ReadOnlySpan<char> password, KeyFile? keyFile)
    {
        var root = PasswordRoot(password, keyFile, RobustArgonSalt, KdfProfile.Normal);
        var layoutKey = new byte[RobustStego.KeySize];
        HKDF.Expand(HashAlgorithmName.SHA256, root, layoutKey, RobustLayoutInfo);
        return (layoutKey, root);
    }

    private static byte[] RobustAeadKey(ReadOnlySpan<byte> root, ReadOnlySpan<byte> salt)
    {
        Span<byte> prk = stackalloc byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, root, salt, prk);
        var key = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, key, RobustAeadInfo);
        CryptographicOperations.ZeroMemory(prk);
        return key;
    }
}
