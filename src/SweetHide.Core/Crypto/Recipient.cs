using System.Diagnostics.CodeAnalysis;

using NSec.Cryptography;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Someone you can hide data for without sharing a password: their X25519 public key, written as
/// <c>sweethide1…</c> (Bech32 with a checksum, so a mistyped or truncated key is rejected rather than used).
/// </summary>
public sealed class Recipient : IEquatable<Recipient>
{
    internal const string HumanReadablePart = "sweethide";
    internal const int KeySize = 32;

    private readonly byte[] _key;

    internal Recipient(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"A public key is {KeySize} bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    internal ReadOnlySpan<byte> Key => _key;

    /// <exception cref="FormatException">Not a valid SweetHide public key.</exception>
    public static Recipient Parse(string text) =>
        TryParse(text, out var recipient) ? recipient : throw new FormatException("Not a valid SweetHide public key (sweethide1…).");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out Recipient? recipient)
    {
        recipient = null;
        if (!Bech32.TryDecode(text?.Trim(), HumanReadablePart, out var key) || key.Length != KeySize || (key[^1] & 0x80) != 0)
        {
            return false;
        }

        var candidate = new Recipient(key);
        if (!candidate.IsUsable())
        {
            return false; // a low-order point: every shared secret would be zero
        }

        recipient = candidate;
        return true;
    }

    public override string ToString() => Bech32.Encode(HumanReadablePart, _key);

    public bool Equals(Recipient? other) => other is not null && _key.AsSpan().SequenceEqual(other._key);

    public override bool Equals(object? obj) => Equals(obj as Recipient);

    public override int GetHashCode() => BitConverter.ToInt32(_key, 0);

    public static bool operator ==(Recipient? left, Recipient? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(Recipient? left, Recipient? right) => !(left == right);

    internal PublicKey ToPublicKey() => PublicKey.Import(KeyAgreementAlgorithm.X25519, _key, KeyBlobFormat.RawPublicKey);

    private bool IsUsable()
    {
        using var probe = NSec.Cryptography.Key.Create(KeyAgreementAlgorithm.X25519);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(probe, ToPublicKey());
        return shared is not null;
    }
}
