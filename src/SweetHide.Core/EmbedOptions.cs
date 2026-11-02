using SweetHide.Core.Crypto;

namespace SweetHide.Core;

/// <summary>How to embed. None of these is stored in clear; the reader learns them from the encrypted header.</summary>
public sealed record EmbedOptions
{
    public static EmbedOptions Default { get; } = new();

    /// <summary>Low bits used in each R, G and B value: 1 (default, invisible) to 4 (more room, visible noise).</summary>
    public int BitsPerChannel { get; init; } = StegoEngine.DefaultBitsPerChannel;

    /// <summary>Password modes only: how expensive each password guess is. Ignored for recipients.</summary>
    public KdfProfile KdfProfile { get; init; } = KdfProfile.Normal;

    /// <summary>
    /// Prefer textured regions, where small changes are hardest to detect. Uses only half of the pixels (a
    /// checkerboard), so it holds less. Not available in deniable mode, which always fills the image.
    /// </summary>
    public bool Adaptive { get; init; }

    internal void Validate()
    {
        if (!StegoEngine.SupportedBitsPerChannel.Contains(BitsPerChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(BitsPerChannel), BitsPerChannel, "Bits per channel must be 1 to 4.");
        }

        if (!Enum.IsDefined(KdfProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(KdfProfile), KdfProfile, "Unknown KDF profile.");
        }
    }
}
