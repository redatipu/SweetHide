using SweetHide.Core.Imaging;
using SweetHide.Forensics.Barcodes;

using ZXing;
using ZXing.Common;
using ZXing.OneD;
using ZXing.QrCode;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class BarcodeScannerTests
{
    private static BitMatrix Qr(string text) => new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0);

    /// <summary>Draws a code in black on white, <paramref name="scale"/> pixels per module.</summary>
    private static PixelBuffer Visible(BitMatrix matrix, int scale)
    {
        var image = Solid(matrix.Width * scale, matrix.Height * scale, 255, 255, 255);
        Paint(matrix, scale, (pixel, black) =>
        {
            if (black)
            {
                image.Pixels[pixel] = image.Pixels[pixel + 1] = image.Pixels[pixel + 2] = 0;
            }
        }, image.Width);
        return image;
    }

    /// <summary>Writes the code into one bit of one channel of a photo: invisible to the eye.</summary>
    private static PixelBuffer HiddenInBit(BitMatrix matrix, int scale, int channel, int bit)
    {
        var image = Photo(matrix.Width * scale + 20, matrix.Height * scale + 20, seed: 8);
        image.Pixels[3] = 254; // not fully opaque, so the alpha planes are scanned too
        Paint(matrix, scale, (pixel, black) =>
        {
            var value = image.Pixels[pixel + channel];
            image.Pixels[pixel + channel] = (byte)(black ? value & ~(1 << bit) : value | (1 << bit));
        }, image.Width);
        return image;
    }

    private static void Paint(BitMatrix matrix, int scale, Action<int, bool> set, int width)
    {
        for (var y = 0; y < matrix.Height * scale; y++)
        {
            for (var x = 0; x < matrix.Width * scale; x++)
            {
                set(((y * width) + x) * 4, matrix[x / scale, y / scale]);
            }
        }
    }

    [Fact]
    public void Reads_a_visible_qr_code()
    {
        var result = Assert.Single(BarcodeScanner.Scan(Visible(Qr("flag{plain_sight}"), 4), cancellationToken: Ct));

        Assert.Equal(new BarcodeResult("QR_CODE", "flag{plain_sight}", "image"), result);
    }

    [Fact]
    public void Reads_a_one_dimensional_barcode()
    {
        var code = new Code128Writer().encode("SWEETHIDE-128", BarcodeFormat.CODE_128, 300, 60);

        var result = Assert.Single(BarcodeScanner.Scan(Visible(code, 1), cancellationToken: Ct));

        Assert.Equal(("CODE_128", "SWEETHIDE-128"), (result.Format, result.Text));
    }

    [Theory]
    [InlineData(0, 0, "R bit 0")]
    [InlineData(2, 1, "B bit 1")]
    [InlineData(3, 0, "A bit 0")]
    public void Finds_a_qr_code_hidden_in_a_bit_plane(int channel, int bit, string expectedSource)
    {
        var image = HiddenInBit(Qr("flag{look_at_the_planes}"), 4, channel, bit);

        var results = BarcodeScanner.Scan(image, cancellationToken: Ct);

        var result = Assert.Single(results);
        Assert.Equal(("QR_CODE", "flag{look_at_the_planes}", expectedSource), (result.Format, result.Text, result.Source));
    }

    [Fact]
    public void Plane_budget_limits_the_higher_bits_only()
    {
        var inBit1 = HiddenInBit(Qr("flag{budget}"), 4, 0, 1);
        var inBit0 = HiddenInBit(Qr("flag{budget}"), 4, 0, 0);

        Assert.Empty(BarcodeScanner.Scan(inBit1, new BarcodeScanOptions { PlanePixelBudget = 0 }, Ct));
        Assert.Single(BarcodeScanner.Scan(inBit1, new BarcodeScanOptions { PlanePixelBudget = inBit1.PixelCount * 4 }, Ct)); // one whole level, RGBA
        Assert.Single(BarcodeScanner.Scan(inBit0, new BarcodeScanOptions { PlanePixelBudget = 0 }, Ct)); // bit 0 is always scanned
        Assert.Empty(BarcodeScanner.Scan(inBit0, new BarcodeScanOptions { ScanBitPlanes = false }, Ct));
    }

    [Fact]
    public void Ordinary_images_have_no_codes()
    {
        Assert.Empty(BarcodeScanner.Scan(Photo(200, 150), cancellationToken: Ct));
        Assert.Empty(BarcodeScanner.Scan(Solid(10, 10, 0, 0, 0), cancellationToken: Ct));
    }
}
