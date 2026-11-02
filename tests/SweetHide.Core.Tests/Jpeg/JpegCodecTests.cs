using FsCheck;
using FsCheck.Xunit;

using SkiaSharp;

using SweetHide.Core.Imaging;
using SweetHide.Core.Jpeg;

namespace SweetHide.Core.Tests.Jpeg;

public class JpegCodecTests
{
    [Theory]
    [InlineData(97, 61, JpegSubsampling.Yuv420, false, 0)]
    [InlineData(97, 61, JpegSubsampling.Yuv444, false, 0)]
    [InlineData(97, 61, JpegSubsampling.Yuv420, true, 0)]
    [InlineData(64, 48, JpegSubsampling.Yuv444, true, 0)]
    [InlineData(100, 37, JpegSubsampling.Yuv420, false, 3)]
    [InlineData(33, 70, JpegSubsampling.Yuv444, true, 1)]
    [InlineData(1, 1, JpegSubsampling.Yuv420, false, 0)]
    [InlineData(17, 1, JpegSubsampling.Yuv444, true, 2)]
    public void Our_encoder_and_decoder_agree_on_every_coefficient(int width, int height, JpegSubsampling subsampling, bool optimize, int restartInterval)
    {
        var coefficients = JpegEncoder.Quantize(JpegTestImages.Textured(width, height, seed: width), 85, subsampling);
        var file = JpegTestImages.Write(coefficients, new JpegWriteOptions { OptimizeHuffmanTables = optimize, RestartInterval = restartInterval });

        var back = JpegDecoder.Read(file);

        JpegTestImages.AssertSameCoefficients(coefficients, back);
    }

    [Theory]
    [InlineData(JpegSubsampling.Yuv420, 75)]
    [InlineData(JpegSubsampling.Yuv444, 92)]
    [InlineData(JpegSubsampling.Yuv420, 30)]
    public void Skia_decodes_our_files_close_to_our_own_idct(JpegSubsampling subsampling, int quality)
    {
        var coefficients = JpegEncoder.Quantize(JpegTestImages.Textured(203, 131), quality, subsampling);
        var file = JpegTestImages.Write(coefficients, new JpegWriteOptions { OptimizeHuffmanTables = true, RestartInterval = 5 });

        using (var codec = SKCodec.Create(new MemoryStream(file)))
        {
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.Equal(new SKSizeI(203, 131), codec.Info.Size);
        }

        var (mean, max) = JpegTestImages.Difference(JpegTestImages.SkiaDecode(file), coefficients.ToPixels());

        Assert.True(mean < 0.75, $"mean difference {mean:F3}");
        Assert.True(max <= 6, $"max difference {max}");
    }

    [Theory]
    [InlineData(SKJpegEncoderDownsample.Downsample420, 90)]
    [InlineData(SKJpegEncoderDownsample.Downsample422, 70)]
    [InlineData(SKJpegEncoderDownsample.Downsample444, 50)]
    public void Rewriting_a_skia_jpeg_keeps_its_pixels_exactly(SKJpegEncoderDownsample downsample, int quality)
    {
        var original = JpegTestImages.SkiaJpeg(JpegTestImages.Textured(150, 99, seed: 3), quality, downsample);
        var coefficients = JpegDecoder.Read(original);

        // Same coefficients and tables => libjpeg-turbo must reconstruct the very same pixels.
        var rewritten = JpegTestImages.Write(coefficients, new JpegWriteOptions { OptimizeHuffmanTables = true });

        JpegTestImages.AssertSameCoefficients(coefficients, JpegDecoder.Read(rewritten));
        Assert.Equal(JpegTestImages.SkiaDecode(original).Pixels.ToArray(), JpegTestImages.SkiaDecode(rewritten).Pixels.ToArray());

        var (mean, max) = JpegTestImages.Difference(JpegTestImages.SkiaDecode(original), coefficients.ToPixels());
        Assert.True(mean < 0.75 && max <= 6, $"preview differs from Skia: mean {mean:F3}, max {max}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(100)]
    public void Quality_scaling_matches_libjpeg(int quality)
    {
        var skia = JpegDecoder.Read(JpegTestImages.SkiaJpeg(TestImages.Photo(16, 16), quality));
        var ours = JpegEncoder.Quantize(TestImages.Photo(16, 16), quality);

        for (var t = 0; t < 2; t++)
        {
            Assert.Equal(skia.GetQuantizationTable(t).ToArray(), ours.GetQuantizationTable(t).ToArray());
        }
    }

    [Fact]
    public void Our_quantization_is_close_to_libjpeg()
    {
        // Same tables and transform: coefficients should only differ through rounding at the edges of a step.
        var image = JpegTestImages.Textured(64, 64, seed: 9);
        var skia = JpegDecoder.Read(JpegTestImages.SkiaJpeg(image, 80, SKJpegEncoderDownsample.Downsample444));
        var ours = JpegEncoder.Quantize(image, 80, JpegSubsampling.Yuv444);

        long total = 0, differing = 0;
        for (var c = 0; c < 3; c++)
        {
            var a = skia.Components[c].Coefficients;
            var b = ours.Components[c].Coefficients;
            for (var i = 0; i < a.Length; i++)
            {
                total++;
                Assert.InRange(a[i] - b[i], -1, 1);
                differing += a[i] != b[i] ? 1 : 0;
            }
        }

        Assert.True(differing < total / 50, $"{differing} of {total} coefficients differ");
    }

    [Fact]
    public void Reads_grayscale_files()
    {
        var file = JpegTestImages.SkiaGrayJpeg(77, 45, 85, seed: 2);

        var coefficients = JpegDecoder.Read(file);

        Assert.Single(coefficients.Components);
        var rewritten = JpegTestImages.Write(coefficients, new JpegWriteOptions { RestartInterval = 4 });
        JpegTestImages.AssertSameCoefficients(coefficients, JpegDecoder.Read(rewritten));
        Assert.Equal(JpegTestImages.SkiaDecode(file).Pixels.ToArray(), JpegTestImages.SkiaDecode(rewritten).Pixels.ToArray());
        var (mean, max) = JpegTestImages.Difference(JpegTestImages.SkiaDecode(file), coefficients.ToPixels());
        Assert.True(mean < 0.5 && max <= 2, $"mean {mean:F3}, max {max}");
    }

    [Fact]
    public void Writes_no_metadata_and_a_plain_jfif_header()
    {
        var file = JpegTestImages.Write(JpegEncoder.Quantize(TestImages.Photo(40, 30), 80));

        var markers = Markers(file);

        Assert.Equal([0xD8, 0xE0, 0xDB, 0xDB, 0xC0, 0xC4, 0xC4, 0xC4, 0xC4, 0xDA, 0xD9], markers);
        var withoutJfif = JpegTestImages.Write(JpegEncoder.Quantize(TestImages.Photo(40, 30), 80), new JpegWriteOptions { WriteJfifHeader = false });
        Assert.DoesNotContain((byte)0xE0, Markers(withoutJfif));
    }

    [Fact]
    public void Optimized_tables_make_smaller_files_with_the_same_coefficients()
    {
        var coefficients = JpegEncoder.Quantize(JpegTestImages.Textured(160, 120), 75);

        var standard = JpegTestImages.Write(coefficients);
        var optimized = JpegTestImages.Write(coefficients, new JpegWriteOptions { OptimizeHuffmanTables = true });

        Assert.True(optimized.Length < standard.Length, $"{optimized.Length} >= {standard.Length}");
        JpegTestImages.AssertSameCoefficients(JpegDecoder.Read(standard), JpegDecoder.Read(optimized));
    }

    [Fact]
    public void Very_low_quality_tables_are_written_as_extended_sequential()
    {
        // Quality 1 gives table entries above 255 only without force_baseline; ours clamps, so build such a table by hand.
        var coefficients = JpegEncoder.Quantize(TestImages.Photo(24, 24), 50);
        var file = JpegTestImages.Write(coefficients);
        var patched = WithLuminanceTableEntry(coefficients, 300);

        var output = JpegTestImages.Write(patched);

        Assert.Contains((byte)0xC1, Markers(output));
        Assert.Equal(300, JpegDecoder.Read(output).GetQuantizationTable(0)[63]);
        Assert.Contains((byte)0xC0, Markers(file));
    }

    [Fact]
    public void Clone_is_independent()
    {
        var original = JpegEncoder.Quantize(TestImages.Photo(32, 32), 80);
        var copy = original.Clone();

        copy.Components[0].Block(0, 0)[5] = 77;

        Assert.NotEqual(77, original.Components[0].Block(0, 0)[5]);
    }

    [Fact]
    public void Rejects_unsupported_kinds_of_jpeg_with_a_clear_message()
    {
        var file = JpegTestImages.Write(JpegEncoder.Quantize(TestImages.Photo(32, 32), 80));
        var sof = IndexOfMarker(file, 0xC0);

        Assert.Contains("Progressive", Reject(Patch(file, sof + 1, 0xC2)).Message, StringComparison.Ordinal);
        Assert.Contains("Arithmetic", Reject(Patch(file, sof + 1, 0xC9)).Message, StringComparison.Ordinal);
        Assert.Contains("Lossless", Reject(Patch(file, sof + 1, 0xC3)).Message, StringComparison.Ordinal);
        Assert.Contains("12-bit", Reject(Patch(file, sof + 4, 12)).Message, StringComparison.Ordinal);
        Assert.Contains("sampling", Reject(Patch(file, sof + 11, 0x31)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_garbage_truncation_and_bombs()
    {
        var file = JpegTestImages.Write(JpegEncoder.Quantize(JpegTestImages.Textured(64, 64), 80));
        var sof = IndexOfMarker(file, 0xC0);

        Reject([]);
        Reject([0xFF, 0xD8]);
        Reject([1, 2, 3, 4, 5]);
        Reject(file[..(file.Length / 2)]);
        Reject(file[..(sof + 6)]);

        // 65535 x 65535 declared in a tiny file: refused by the pixel limit, and by the size check without it.
        var huge = Patch(Patch(Patch(Patch(file, sof + 5, 0xFF), sof + 6, 0xFF), sof + 7, 0xFF), sof + 8, 0xFF);
        Assert.Contains("limit", Reject(huge).Message, StringComparison.Ordinal);
        Assert.Contains("too short", Assert.Throws<InvalidImageException>(() => JpegDecoder.Read(huge, maxPixels: long.MaxValue)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidImageException>(() => JpegDecoder.Read(file, maxPixels: 4000));
    }

    [Property(MaxTest = 200)]
    public void Corrupted_files_either_decode_or_fail_cleanly(PositiveInt seed)
    {
        var file = (byte[])CorruptionBase.Clone();
        var random = new Random(seed.Get);
        var edits = random.Next(1, 6);
        for (var i = 0; i < edits; i++)
        {
            file[random.Next(2, file.Length)] = (byte)random.Next(256);
        }

        try
        {
            JpegDecoder.Read(file).ToPixels();
        }
        catch (InvalidImageException)
        {
            // Expected for most corruptions; anything else (IndexOutOfRange, overflow...) fails the test.
        }
    }

    private static readonly byte[] CorruptionBase =
        JpegTestImages.Write(JpegEncoder.Quantize(JpegTestImages.Textured(40, 24), 70), new JpegWriteOptions { RestartInterval = 2 });

    private static InvalidImageException Reject(byte[] file) => Assert.Throws<InvalidImageException>(() => JpegDecoder.Read(file));

    private static byte[] Patch(byte[] file, int offset, byte value)
    {
        var copy = (byte[])file.Clone();
        copy[offset] = value;
        return copy;
    }

    private static int IndexOfMarker(byte[] file, byte marker)
    {
        for (var i = 0; i + 1 < file.Length; i++)
        {
            if (file[i] == 0xFF && file[i + 1] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Marker not found.");
    }

    private static List<byte> Markers(byte[] file)
    {
        var markers = new List<byte> { file[1] };
        var position = 2;
        while (position + 2 <= file.Length && file[position] == 0xFF)
        {
            var marker = file[position + 1];
            markers.Add(marker);
            if (marker == 0xD9)
            {
                break;
            }

            var length = (file[position + 2] << 8) | file[position + 3];
            position += 2 + length;
            if (marker == 0xDA)
            {
                // Skip entropy-coded data up to the next real marker.
                while (position + 1 < file.Length && !(file[position] == 0xFF && file[position + 1] is not (0x00 or >= 0xD0 and <= 0xD7)))
                {
                    position++;
                }
            }
        }

        return markers;
    }

    private static JpegCoefficients WithLuminanceTableEntry(JpegCoefficients source, int value)
    {
        // Round-trip through a hand-patched 16-bit DQT so the decoder builds the table for us.
        using var stream = new MemoryStream();
        JpegEncoder.Write(source, stream);
        var file = stream.ToArray();
        var dqt = IndexOfMarker(file, 0xDB);
        var patched = new List<byte>(file[..dqt]);
        patched.AddRange([0xFF, 0xDB, 0x00, 2 + 1 + 128, 0x10]);
        var table = source.GetQuantizationTable(0).ToArray();
        table[63] = (ushort)value;
        foreach (var natural in new byte[] { 0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63 })
        {
            patched.Add((byte)(table[natural] >> 8));
            patched.Add((byte)table[natural]);
        }

        var dqtLength = (file[dqt + 2] << 8) | file[dqt + 3];
        patched.AddRange(file[(dqt + 2 + dqtLength)..]);
        return JpegDecoder.Read(patched.ToArray());
    }
}
