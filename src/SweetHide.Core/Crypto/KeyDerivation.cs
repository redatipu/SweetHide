using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace SweetHide.Core.Crypto;

/// <summary>An AES-256-GCM key and a key for a slot permutation.</summary>
internal sealed class DerivedKeys : IDisposable
{
    public DerivedKeys(byte[] aeadKey, byte[] permutationKey)
    {
        AeadKey = aeadKey;
        PermutationKey = permutationKey;
    }

    public byte[] AeadKey { get; }

    public byte[] PermutationKey { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(AeadKey);
        CryptographicOperations.ZeroMemory(PermutationKey);
    }
}

/// <summary>
/// Password → keys. Both format versions start from the same Argon2id master key, so when an image's v2 profile
/// uses the v1 parameters one Argon2id run serves both readers.
/// </summary>
internal static class KeyDerivation
{
    public const int MasterKeySize = 32;
    public const int RootKeySize = 32;
    public const int AeadKeySize = 32;
    public const int PermutationKeySize = 64;

    private static readonly byte[] V1AeadInfo = "sweethide/v1/aead-key"u8.ToArray();
    private static readonly byte[] V1PermutationInfo = "sweethide/v1/slot-permutation"u8.ToArray();
    private static readonly byte[] V2AeadInfo = "sweethide/v2/aead-key"u8.ToArray();
    private static readonly byte[] V2PermutationInfo = "sweethide/v2/slot-permutation"u8.ToArray();

    /// <summary>Format v1: Argon2id(password, salt) → HKDF-SHA256-Expand into independent sub-keys.</summary>
    public static DerivedKeys Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        var master = Argon2Master(password, salt, parameters);
        try
        {
            return V1Keys(master);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Argon2id over the UTF-8 bytes of the NFC-normalized password.</summary>
    public static byte[] Argon2Master(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        parameters.Validate();
        if (password.IsEmpty)
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        // NFC so the same Thai/accented text typed via different input methods yields the same key.
        // (Best effort: the normalized string itself cannot be wiped from managed memory.)
        var normalized = password.ToString().Normalize(NormalizationForm.FormC);
        var passwordBytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            using var argon = new Argon2id(passwordBytes);
            argon.Salt = salt.ToArray();
            argon.MemorySize = parameters.MemoryKiB;
            argon.Iterations = parameters.Iterations;
            argon.DegreeOfParallelism = parameters.Parallelism;
            return argon.GetBytes(MasterKeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static DerivedKeys V1Keys(ReadOnlySpan<byte> master) => Expand(master, V1AeadInfo, V1PermutationInfo);

    /// <summary>
    /// Format v2 password mode: <c>root = HKDF-Extract(salt = keyFileDigest or 32 zero bytes, IKM = master)</c>.
    /// The key file is mixed in after Argon2id, so it adds a 256-bit factor without changing the password cost.
    /// </summary>
    public static byte[] PasswordRoot(ReadOnlySpan<byte> master, KeyFile? keyFile)
    {
        Span<byte> zero = stackalloc byte[32];
        zero.Clear();
        return HMACSHA256.HashData(keyFile is null ? zero : keyFile.Digest, master);
    }

    /// <summary>Format v2: the container's AEAD key and slot-permutation key, from a 32-byte root key.</summary>
    public static DerivedKeys ContainerKeys(ReadOnlySpan<byte> root) => Expand(root, V2AeadInfo, V2PermutationInfo);

    private static DerivedKeys Expand(ReadOnlySpan<byte> prk, byte[] aeadInfo, byte[] permutationInfo)
    {
        var aead = new byte[AeadKeySize];
        var permutation = new byte[PermutationKeySize];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, aead, aeadInfo);
        HKDF.Expand(HashAlgorithmName.SHA256, prk, permutation, permutationInfo);
        return new DerivedKeys(aead, permutation);
    }
}
