using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core;

/// <summary>
/// Palette images (8-bit PNGs, the kind GIF-style graphics and optimised screenshots use). The output stays a palette
/// image with the very same palette, so its file type and colours give nothing away, but each pixel carries only one
/// bit and pixels whose colour has no look-alike in the palette carry nothing. See <see cref="PaletteCarrier"/>.
/// </summary>
/// <remarks>Password mode (with an optional key file and any KDF profile) only; not adaptive, 1 bit per pixel.</remarks>
public sealed partial class StegoEngine
{
    /// <summary>Capacity of a palette image in password mode.</summary>
    public static CapacityInfo GetCapacity(IndexedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var carrier = new PaletteCarrier(image);
        var bytes = ContainerBytes(new V2Layout(carrier), V2Layout.PublicSlots, ContainerPlacement.Whole, EmbedOptions.Default);
        return new CapacityInfo(image.Width, image.Height, carrier.UsablePixels, 1, Math.Max(0, bytes));
    }

    /// <summary>Returns a new palette image carrying <paramref name="payload"/>. The cover is not modified.</summary>
    /// <param name="cover">The palette cover.</param>
    /// <param name="payload">What to hide.</param>
    /// <param name="password">The password.</param>
    /// <param name="keyFile">Optional second factor.</param>
    /// <param name="options">Only <see cref="EmbedOptions.KdfProfile"/> applies; bits must be 1 and adaptive off.</param>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public IndexedImage Embed(IndexedImage cover, Payload payload, ReadOnlySpan<char> password, KeyFile? keyFile = null, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(payload);
        options ??= EmbedOptions.Default;
        options.Validate();
        if (options.BitsPerChannel != 1 || options.Adaptive)
        {
            throw new ArgumentException("Palette images carry exactly one bit per pixel and do not support adaptive embedding.", nameof(options));
        }

        RequirePassword(password);

        var stego = new IndexedImage(cover.Width, cover.Height, (byte[])cover.Indices.Clone(), cover.Palette.ToArray());
        var content = PayloadSerializer.Serialize(payload);
        try
        {
            EmbedPasswordInto(new PaletteCarrier(stego), content, ContentKind.Payload, password, keyFile, options);
            return stego;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>Reveals password-mode data from a palette image.</summary>
    /// <exception cref="StegoException">
    /// <see cref="StegoError.NotFoundOrWrongPassword"/>, <see cref="StegoError.Corrupted"/> or
    /// <see cref="StegoError.UnsupportedFormat"/>.
    /// </exception>
    public RevealResult Reveal(IndexedImage image, ReadOnlySpan<char> password, KeyFile? keyFile = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequirePassword(password);
        return RevealPassword(new PaletteCarrier(image), password, keyFile);
    }

    /// <inheritdoc cref="Reveal(IndexedImage, ReadOnlySpan{char}, KeyFile?)"/>
    public Payload Extract(IndexedImage image, ReadOnlySpan<char> password, KeyFile? keyFile = null) =>
        PayloadOf(Reveal(image, password, keyFile));
}
