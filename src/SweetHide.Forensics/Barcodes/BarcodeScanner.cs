using System.Globalization;

using SweetHide.Core.Imaging;

using ZXing;
using ZXing.Common;

namespace SweetHide.Forensics.Barcodes;

/// <param name="Format">Barcode format as ZXing names it, e.g. "QR_CODE", "CODE_128".</param>
/// <param name="Text">Decoded content.</param>
/// <param name="Source">Where it was found: "image", or a bit plane such as "R bit 0".</param>
public sealed record BarcodeResult(string Format, string Text, string Source);

public sealed record BarcodeScanOptions
{
    /// <summary>Also render each channel's bit planes and scan those (QR codes hidden in a single LSB plane).</summary>
    public bool ScanBitPlanes { get; init; } = true;

    public IReadOnlyList<int> BitPlanes { get; init; } = [0, 1, 2, 3, 4, 5, 6, 7];

    /// <summary>
    /// Bit 0 of every channel is always scanned. Higher bit levels follow, one whole level (all channels) at a time,
    /// while their pixels fit in this budget: a 1-megapixel image gets every plane, a 12-megapixel photo only bit 0.
    /// ZXing needs roughly 40 ms per megapixel of noise.
    /// </summary>
    public long PlanePixelBudget { get; init; } = 24L * 1024 * 1024;

    /// <summary>ZXing's slower, more thorough search (rotations, smaller codes).</summary>
    public bool TryHarder { get; init; } = true;

    public static BarcodeScanOptions Default { get; } = new();
}

/// <summary>
/// Finds QR codes and other barcodes with ZXing.Net, in the image as shown and in every bit plane. Bit planes are
/// limited to 2D codes and Code 128, because 1D readers find "barcodes" in noise.
/// </summary>
public static class BarcodeScanner
{
    private static readonly BarcodeFormat[] TwoDimensional =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417, BarcodeFormat.CODE_128,
    ];

    private static readonly BarcodeFormat[] All =
    [
        .. TwoDimensional, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A, BarcodeFormat.UPC_E, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    ];

    private static readonly string[] ChannelNames = ["R", "G", "B", "A"];

    public static IReadOnlyList<BarcodeResult> Scan(PixelBuffer image, BarcodeScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= BarcodeScanOptions.Default;
        var results = new List<BarcodeResult>();
        var seen = new HashSet<(string, string)>();

        Decode(Luminance(image), image, All, options, "image", multiple: true, results, seen);

        if (options.ScanBitPlanes)
        {
            var channels = image.IsOpaque ? 3 : 4;
            var plane = new byte[image.PixelCount];
            var budget = options.PlanePixelBudget;
            foreach (var bit in options.BitPlanes.Order())
            {
                ArgumentOutOfRangeException.ThrowIfNegative(bit);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 7);
                if (bit > 0)
                {
                    budget -= image.PixelCount * channels;
                    if (budget < 0)
                    {
                        break;
                    }
                }

                for (var channel = 0; channel < channels; channel++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (RenderPlane(image, channel, bit, plane))
                    {
                        Decode(plane, image, TwoDimensional, options, string.Create(CultureInfo.InvariantCulture, $"{ChannelNames[channel]} bit {bit}"), multiple: false, results, seen);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>Decodes one luminance rendering. <c>multiple</c> looks for several codes (slower); bit planes use a single-code search.</summary>
    private static void Decode(byte[] luminance, PixelBuffer image, BarcodeFormat[] formats, BarcodeScanOptions options, string source, bool multiple, List<BarcodeResult> results, HashSet<(string, string)> seen)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = options.TryHarder,
                TryInverted = true,
                PossibleFormats = formats,
            },
        };

        var source8 = new RGBLuminanceSource(luminance, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var decoded = multiple ? reader.DecodeMultiple(source8) : reader.Decode(source8) is { } single ? [single] : null;
        foreach (var result in decoded ?? [])
        {
            var format = result.BarcodeFormat.ToString();
            if (result.Text is { } text && seen.Add((format, text)))
            {
                results.Add(new BarcodeResult(format, text, source));
            }
        }
    }

    /// <summary>Greyscale as displayed: Rec. 601 luma composited over white.</summary>
    private static byte[] Luminance(PixelBuffer image)
    {
        ReadOnlySpan<byte> rgba = image.Pixels;
        var luminance = new byte[image.PixelCount];
        for (var p = 0; p < luminance.Length; p++)
        {
            var i = p * 4;
            var luma = ((rgba[i] * 299) + (rgba[i + 1] * 587) + (rgba[i + 2] * 114)) / 1000;
            var alpha = rgba[i + 3];
            luminance[p] = (byte)(((luma * alpha) + (255 * (255 - alpha))) / 255);
        }

        return luminance;
    }

    /// <summary>Renders one bit plane as black/white; returns false when the plane is uniform (nothing to decode).</summary>
    private static bool RenderPlane(PixelBuffer image, int channel, int bit, byte[] plane)
    {
        ReadOnlySpan<byte> rgba = image.Pixels;
        var ones = 0L;
        for (var p = 0; p < plane.Length; p++)
        {
            var value = (rgba[(p * 4) + channel] >> bit) & 1;
            plane[p] = (byte)(value * 255);
            ones += value;
        }

        return ones > 0 && ones < plane.Length;
    }
}
