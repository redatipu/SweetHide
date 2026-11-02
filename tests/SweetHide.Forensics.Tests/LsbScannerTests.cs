using System.Diagnostics;

using SweetHide.Core.Imaging;
using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Lsb;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class LsbScannerTests
{
    public static TheoryData<string> HidingConfigs() => new(
        "bit0,rgb,msb,xy",
        "bit0,bgr,lsb,xy",
        "bit1,r,msb,yx",
        "bit0,rgba,msb,xy",
        "bit2,argb,lsb,yx",
        "bit7,g,msb,xy");

    [Theory]
    [MemberData(nameof(HidingConfigs))]
    public void Finds_nul_terminated_text_in_any_configuration(string configName)
    {
        var config = LsbConfig.Parse(configName);
        var cover = Photo(96, 64, seed: 3);
        if (LsbConfig.UsesAlpha(config.Channels))
        {
            cover.Pixels[3] = 254; // not opaque, so alpha configurations are scanned
        }

        var stego = HideBits(cover, Utf8("flag{every_bit_counts}\0"), config);

        var results = LsbScanner.Scan(stego, null, Ct);

        var top = results[0];
        Assert.Equal(config, top.Config);
        Assert.Equal(ContentKind.TerminatedText, top.Content.Kind);
        Assert.Equal("flag{every_bit_counts}", top.Content.Preview);
        Assert.Equal("flag{every_bit_counts}"u8.ToArray(), LsbScanner.ExtractPayload(stego, top, cancellationToken: Ct));
    }

    [Fact]
    public void Finds_thai_text()
    {
        var config = new LsbConfig(0, LsbChannels.Rgb, PixelOrder.RowMajor, BitOrder.MsbFirst);
        var stego = HideBits(Photo(80, 80, seed: 5), Utf8("รหัสผ่านคือ มะม่วงหิมพานต์\0"), config);

        var top = LsbScanner.Scan(stego, null, Ct)[0];

        Assert.Equal(config, top.Config);
        Assert.Equal("รหัสผ่านคือ มะม่วงหิมพานต์", top.Content.Preview);
    }

    [Fact]
    public void Extracts_a_length_prefixed_file_exactly()
    {
        var hiddenPng = EncodePng(Solid(4, 4, 10, 20, 30));
        var config = new LsbConfig(0, LsbChannels.Rgb, PixelOrder.ColumnMajor, BitOrder.MsbFirst);
        var stego = HideBits(Photo(120, 120, seed: 6), Concat(U32BigEndian((uint)hiddenPng.Length), hiddenPng), config);

        var top = LsbScanner.Scan(stego, null, Ct)[0];

        Assert.Equal(config, top.Config);
        Assert.Equal(ContentKind.LengthPrefixedFile, top.Content.Kind);
        Assert.Equal(FileType.Png, top.Content.FileType);
        Assert.Equal(hiddenPng, LsbScanner.ExtractPayload(stego, top, cancellationToken: Ct));
    }

    [Fact]
    public void Clean_images_produce_no_confident_results()
    {
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var results = LsbScanner.Scan(Photo(200, 150, seed), null, Ct);
            Assert.All(results, r => Assert.True(r.Content.Score < 0.5, $"{r.Config}: {r.Content.Score} {r.Content.Kind} '{r.Content.Preview}'"));
        }

        Assert.Empty(LsbScanner.Scan(Solid(50, 50, 0, 0, 0), null, Ct));
    }

    [Fact]
    public void Min_score_zero_lists_every_configuration()
    {
        var image = Photo(20, 20);
        image.Pixels[3] = 0;

        var results = LsbScanner.Scan(image, new LsbScanOptions { MinScore = 0, MaxResults = int.MaxValue }, Ct);

        Assert.Equal(8 * 8 * 2 * 2, results.Count);
        Assert.Equal(results.Count, results.Select(r => r.Config).Distinct().Count());
    }

    [Fact]
    public void Skips_alpha_configurations_on_opaque_images()
    {
        var results = LsbScanner.Scan(Photo(20, 20), new LsbScanOptions { MinScore = 0, MaxResults = int.MaxValue }, Ct);

        Assert.Equal(8 * 5 * 2 * 2, results.Count);
        Assert.DoesNotContain(results, r => r.Config.Channels is LsbChannels.A or LsbChannels.Rgba or LsbChannels.Argb);
    }

    [Fact]
    public void Stream_capacity_and_extraction_respect_limits()
    {
        var image = Photo(10, 10);
        var rgb = LsbConfig.Parse("bit0,rgb");

        Assert.Equal(37, LsbScanner.StreamCapacity(image, rgb)); // 300 bits
        Assert.Equal(37, LsbScanner.ExtractStream(image, rgb, cancellationToken: Ct).Length);
        Assert.Equal(5, LsbScanner.ExtractStream(image, rgb, 5, Ct).Length);
        Assert.Empty(LsbScanner.ExtractStream(image, rgb, 0, Ct));
    }

    [Fact]
    public void Extracted_stream_matches_what_was_hidden()
    {
        var payload = RandomBytes(500, 12);
        foreach (var config in LsbScanner.Configurations(new LsbScanOptions { BitPlanes = [0, 3] }))
        {
            var stego = HideBits(Photo(64, 64, seed: 7), payload, config);
            Assert.Equal(payload, LsbScanner.ExtractStream(stego, config, payload.Length, Ct));
        }
    }

    [Theory]
    [InlineData("bit0,rgb,msb,xy", 0, LsbChannels.Rgb, PixelOrder.RowMajor, BitOrder.MsbFirst)]
    [InlineData("BIT7,ARGB,LSB,YX", 7, LsbChannels.Argb, PixelOrder.ColumnMajor, BitOrder.LsbFirst)]
    [InlineData("bit3,b", 3, LsbChannels.B, PixelOrder.RowMajor, BitOrder.MsbFirst)]
    [InlineData(" bit1 , g , yx ", 1, LsbChannels.G, PixelOrder.ColumnMajor, BitOrder.MsbFirst)]
    public void Parses_configuration_names(string text, int plane, LsbChannels channels, PixelOrder order, BitOrder bitOrder)
    {
        var config = LsbConfig.Parse(text);

        Assert.Equal(new LsbConfig(plane, channels, order, bitOrder), config);
        Assert.Equal(config, LsbConfig.Parse(config.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bit8,rgb")]
    [InlineData("bit-1,rgb")]
    [InlineData("bit0,3")]
    [InlineData("bit0,rgb,msb,xy,extra")]
    [InlineData("plane0,rgb")]
    [InlineData("bit0,cmyk")]
    [InlineData("bit0,rgb,sideways")]
    public void Rejects_invalid_configuration_names(string text)
    {
        Assert.False(LsbConfig.TryParse(text, out _));
        Assert.Throws<FormatException>(() => LsbConfig.Parse(text));
    }

    [Fact]
    public void Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LsbScanner.Scan(Photo(10, 10), cancellationToken: cts.Token));
    }

    [Fact]
    public void Scans_a_12_megapixel_image_quickly()
    {
        var data = new byte[4000 * 3000 * 4];
        new Random(1).NextBytes(data);
        var image = new PixelBuffer(4000, 3000, data);
        var stego = HideBits(image, Utf8("big image, small secret\0"), LsbConfig.Parse("bit0,rgb,msb,yx"));

        var watch = Stopwatch.StartNew();
        var results = LsbScanner.Scan(stego, cancellationToken: Ct);
        watch.Stop();

        Assert.Equal("bit0,rgb,msb,yx", results[0].Config.Name);
        // Only ~11k pixels are read per configuration; generous bound for slow CI runners.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
