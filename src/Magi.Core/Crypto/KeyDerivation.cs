using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace Magi.Core.Crypto;

/// <summary>Keys derived from one password + salt: an AES-256-GCM key and a key for the slot permutation.</summary>
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

internal static class KeyDerivation
{
    public const int AeadKeySize = 32;
    public const int PermutationKeySize = 64;

    private static readonly byte[] AeadInfo = "magi/v1/aead-key"u8.ToArray();
    private static readonly byte[] PermutationInfo = "magi/v1/slot-permutation"u8.ToArray();

    /// <summary>Argon2id(password, salt) → HKDF-SHA256-Expand into independent sub-keys.</summary>
    public static DerivedKeys Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
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
        byte[]? master = null;
        try
        {
            using (var argon = new Argon2id(passwordBytes))
            {
                argon.Salt = salt.ToArray();
                argon.MemorySize = parameters.MemoryKiB;
                argon.Iterations = parameters.Iterations;
                argon.DegreeOfParallelism = parameters.Parallelism;
                master = argon.GetBytes(32);
            }

            var aeadKey = HKDF.Expand(HashAlgorithmName.SHA256, master, AeadKeySize, AeadInfo);
            var permutationKey = HKDF.Expand(HashAlgorithmName.SHA256, master, PermutationKeySize, PermutationInfo);
            return new DerivedKeys(aeadKey, permutationKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (master is not null)
            {
                CryptographicOperations.ZeroMemory(master);
            }
        }
    }
}
