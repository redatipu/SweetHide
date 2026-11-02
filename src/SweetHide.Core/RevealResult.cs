using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;

namespace SweetHide.Core;

/// <summary>How the revealed data was hidden.</summary>
public enum HidingMode
{
    /// <summary>One payload protected by a password (and optionally a key file).</summary>
    Password = 0,

    /// <summary>One of the two halves of a deniable image. The other half may hold a second payload or nothing.</summary>
    Deniable = 1,

    /// <summary>Encrypted to one or more public keys.</summary>
    Recipients = 2,
}

/// <param name="FormatVersion">1 for images made by v0.1.0, otherwise 2.</param>
/// <param name="Mode">How the data was protected.</param>
/// <param name="BitsPerChannel">Low bits used per colour value.</param>
/// <param name="Adaptive">Whether textured regions were preferred.</param>
/// <param name="KdfProfile">The Argon2id profile, for password modes.</param>
public sealed record HiddenDataInfo(int FormatVersion, HidingMode Mode, int BitsPerChannel, bool Adaptive, KdfProfile? KdfProfile);

/// <summary>What an image revealed: a payload, or one share of a split payload.</summary>
public sealed class RevealResult
{
    internal RevealResult(HiddenDataInfo info, Payload? payload, ShamirShare? share)
    {
        Info = info;
        Payload = payload;
        Share = share;
    }

    public HiddenDataInfo Info { get; }

    /// <summary>The hidden payload, or null when the image holds a <see cref="Share"/>.</summary>
    public Payload? Payload { get; }

    /// <summary>One share of a split payload; combine enough of them with <see cref="StegoEngine.CombineShares"/>.</summary>
    public ShamirShare? Share { get; }
}
