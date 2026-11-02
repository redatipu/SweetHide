using System.Security.Cryptography;

using SweetHide.Core.Stego;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Tells the reader which <see cref="KdfProfile"/> to run without writing it in clear: the profile is a function of
/// the salt, <c>code = SHA-256("sweethide/v2/kdf-profile" ‖ salt)[0] &amp; 3</c>, mapped 0 → Fast, 1 → Normal,
/// 2 → Paranoid, 3 → Normal. The writer draws salts until one maps to the chosen profile.
/// </summary>
/// <remarks>
/// The stored salt is still uniformly random among the salts with that code, and every code is valid, so no salt
/// marks an image as "not SweetHide". What remains is inherent to any public hint: anyone can compute the code of
/// any image, and for real images it follows people's profile choices rather than being uniform. With Normal on half
/// of the codes, a Normal image gains an observer at most a factor of 2 in likelihood (one bit), and the profile
/// itself is no secret under Kerckhoffs' principle. The alternative, trying every profile, would cost every reader
/// the sum of all three Argon2id runs on each wrong password.
/// </remarks>
internal static class ProfileCode
{
    public const int SaltSize = 16;

    private static readonly byte[] Label = "sweethide/v2/kdf-profile"u8.ToArray();

    public static KdfProfile FromSalt(ReadOnlySpan<byte> salt)
    {
        Span<byte> input = stackalloc byte[Label.Length + SaltSize];
        Label.CopyTo(input);
        salt[..SaltSize].CopyTo(input[Label.Length..]);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return (hash[0] & 3) switch
        {
            0 => KdfProfile.Fast,
            2 => KdfProfile.Paranoid,
            _ => KdfProfile.Normal,
        };
    }

    /// <summary>Fills <paramref name="salt"/> with random bytes that select <paramref name="profile"/> (2 to 4 draws on average).</summary>
    public static void NewSalt(Span<byte> salt, KdfProfile profile, IRandomSource random)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown KDF profile.");
        }

        do
        {
            random.Fill(salt[..SaltSize]);
        }
        while (FromSalt(salt) != profile);
    }
}
