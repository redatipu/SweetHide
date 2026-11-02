using System.Buffers.Binary;
using System.Text.Json;

using FsCheck;
using FsCheck.Fluent;

using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Lsb;
using SweetHide.Forensics.Steganalysis;

using ZXing;
using ZXing.QrCode;

using static SweetHide.Forensics.Tests.FileBuilders;
using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class StegoFinderTests
{
    private static readonly StegoFinder Finder = new();

    private static byte[] LayeredPng(out byte[] zip)
    {
        var pixels = HideBits(Photo(120, 120, seed: 21), Utf8("flag{lsb_layer}\0"), LsbConfig.Parse("bit0,rgb,msb,xy"));
        zip = Zip(("flag.txt", "flag{zip_layer}"));
        var comment = Convert.ToBase64String(Utf8("flag{in_the_comment}"));
        return Concat(PngOf(pixels, ("tEXt", TextChunk("Comment", comment))), zip);
    }

    [Fact]
    public void Finds_every_layer_of_a_layered_png()
    {
        var file = LayeredPng(out var zip);

        var report = Finder.Analyze(file, "layered.png", Ct);

        Assert.Equal(("layered.png", "PNG image", 120, 120), (report.FileName, report.Format, report.Width, report.Height));
        Assert.Empty(report.Errors);

        var lsb = Assert.Single(report.Findings, f => f.Kind == FindingKind.LsbPayload);
        Assert.Contains("flag{lsb_layer}", lsb.Detail, StringComparison.Ordinal);
        Assert.Equal("bit0,rgb,msb,xy", lsb.Source);
        Assert.Equal("flag{lsb_layer}"u8.ToArray(), lsb.Payload);
        Assert.Equal("lsb_bit0_rgb_msb_xy.txt", lsb.SuggestedFileName);
        Assert.True(lsb.Confidence >= 0.9);

        Assert.Contains(report.Findings, f => f is { Kind: FindingKind.Metadata, Title: "PNG tEXt: Comment" });
        var decoded = Assert.Single(report.Findings, f => f.Kind == FindingKind.EncodedText && f.Detail == "flag{in_the_comment}");
        Assert.Equal("Decodes with Base64", decoded.Title);
        Assert.True(decoded.Confidence >= 0.9);

        var trailing = Assert.Single(report.Findings, f => f.Kind == FindingKind.TrailingData);
        Assert.Equal(zip, trailing.Payload);
        Assert.EndsWith(".zip", trailing.SuggestedFileName, StringComparison.Ordinal);
        Assert.Equal(file.Length - zip.Length, trailing.Offset);

        Assert.Equal(report.Findings.OrderByDescending(f => f.Confidence), report.Findings);
    }

    [Fact]
    public void Reports_an_exif_flag_once()
    {
        var exif = new TiffBuilder().Ascii("IFD0", 0x010E, "flag{exif_once}").Build();
        var jpeg = Jpeg((0xE1, Concat(Utf8("Exif\0\0"), exif)));

        var report = Finder.Analyze(jpeg, cancellationToken: Ct);

        var field = Assert.Single(report.Findings, f => f.Detail.Contains("flag{exif_once}", StringComparison.Ordinal));
        Assert.Equal(FindingKind.Metadata, field.Kind);
        Assert.Equal(0.95, field.Confidence);
    }

    [Fact]
    public void Finds_a_qr_code_in_a_bit_plane()
    {
        var matrix = new QRCodeWriter().encode("flag{qr_in_red}", BarcodeFormat.QR_CODE, 0, 0);
        var image = Photo((matrix.Width * 3) + 10, (matrix.Height * 3) + 10, seed: 2);
        for (var y = 0; y < matrix.Height * 3; y++)
        {
            for (var x = 0; x < matrix.Width * 3; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                image.Pixels[i] = (byte)((image.Pixels[i] & 0xFE) | (matrix[x / 3, y / 3] ? 0 : 1));
            }
        }

        var report = Finder.Analyze(EncodePng(image), cancellationToken: Ct);

        var barcode = Assert.Single(report.Findings, f => f.Kind == FindingKind.Barcode);
        Assert.Equal(("QR_CODE hidden in R bit 0", "flag{qr_in_red}", 0.95), (barcode.Title, barcode.Detail, barcode.Confidence));
    }

    [Fact]
    public void Notices_an_edited_png_height_even_if_the_image_will_not_decode()
    {
        var png = Png(16, 16);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 10);

        var report = Finder.Analyze(png, cancellationToken: Ct);

        var anomaly = Assert.Single(report.Findings, f => f.Title == "Image dimensions were edited");
        Assert.Contains("16x16", anomaly.Detail, StringComparison.Ordinal);
        Assert.Contains("16x10", anomaly.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyses_wav_files()
    {
        var samples = Tone(8000, 1, 8000, 440);
        var message = Utf8("flag{wav_lsb}\0");
        for (var i = 0; i < message.Length * 8; i++)
        {
            samples[i] = (short)((samples[i] & ~1) | ((message[i / 8] >> (7 - (i % 8))) & 1));
        }

        var report = Finder.Analyze(Wav(1, 8000, 16, Pcm16(samples)), "song.wav", Ct);

        Assert.Equal("WAV audio", report.Format);
        var lsb = Assert.Single(report.Findings, f => f.Kind == FindingKind.AudioLsb);
        Assert.Equal("flag{wav_lsb}"u8.ToArray(), lsb.Payload);
        var spectrogram = Assert.Single(report.Findings, f => f.Kind == FindingKind.Spectrogram);
        using var png = new MemoryStream(spectrogram.Payload!);
        Assert.Equal(512, Codec.Decode(png, applyOrientation: false).Height);
    }

    [Fact]
    public void Flags_heavy_lsb_replacement()
    {
        var report = Finder.Analyze(EncodePng(ReplaceLsbs(Photo(200, 200, seed: 12), 0.6, 5)), cancellationToken: Ct);

        var finding = Assert.Single(report.Findings, f => f.Kind == FindingKind.Steganalysis);
        Assert.True(finding.Confidence >= 0.7, $"{finding.Title} ({finding.Confidence})");
        Assert.StartsWith("About ", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_images_have_no_confident_findings()
    {
        var photo = Photo(160, 120, seed: 31);
        var jpeg = new MemoryStream();
        using (var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(160, 120, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul)))
        {
            System.Runtime.InteropServices.Marshal.Copy(photo.Pixels.ToArray(), 0, bitmap.GetPixels(), photo.Pixels.Length);
            bitmap.Encode(jpeg, SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
        }

        foreach (var file in new[] { EncodePng(photo), jpeg.ToArray() })
        {
            var report = Finder.Analyze(file, cancellationToken: Ct);
            Assert.Empty(report.Errors);
            Assert.All(report.Findings, f => Assert.True(f.Confidence < 0.5, $"{f.Kind} {f.Title}: {f.Detail} ({f.Confidence})"));
        }
    }

    [Fact]
    public void Ignores_flag_shapes_inside_compressed_pixel_data_unless_they_are_real_flags()
    {
        // An extra IDAT chunk standing in for compressed data that happens to contain printable runs.
        var png = Png(8, 8, ("IDAT", Utf8("\u0001xq{zt9}\u0002 flag{in_the_idat} \u0003")), ("stEg", Utf8("\u0001ab{cd_ef}\u0002")));

        var strings = Finder.Analyze(png, cancellationToken: Ct).Findings.Where(f => f.Kind == FindingKind.InterestingString).ToList();

        Assert.Contains(strings, f => f.Detail == "flag{in_the_idat}");
        Assert.DoesNotContain(strings, f => f.Detail == "xq{zt9}");
        Assert.Contains(strings, f => f.Detail == "ab{cd_ef}");
    }

    [Fact]
    public void Garbage_and_oversized_input_are_handled()
    {
        var garbage = Finder.Analyze(RandomBytes(5000, 4), cancellationToken: Ct);
        Assert.Equal("unknown", garbage.Format);
        Assert.Null(garbage.Width);
        Assert.Empty(garbage.Errors);

        var tooBig = new StegoFinder(new StegoFinderOptions { MaxFileBytes = 10 }).Analyze(new byte[11], cancellationToken: Ct);
        Assert.Empty(tooBig.Findings);
        Assert.Single(tooBig.Errors);

        var empty = Finder.Analyze(ReadOnlyMemory<byte>.Empty, cancellationToken: Ct);
        Assert.Equal(0, empty.FileSize);
    }

    [Fact]
    public void Options_switch_checks_off_and_filter_findings()
    {
        var file = LayeredPng(out _);
        var quiet = new StegoFinder(new StegoFinderOptions { ScanLsb = false, ScanBarcodes = false, RunSteganalysis = false, MinConfidence = 0.5 });

        var report = quiet.Analyze(file, cancellationToken: Ct);

        Assert.DoesNotContain(report.Findings, f => f.Kind is FindingKind.LsbPayload or FindingKind.Barcode or FindingKind.Steganalysis);
        Assert.All(report.Findings, f => Assert.True(f.Confidence >= 0.5));
        Assert.Contains(report.Findings, f => f.Kind == FindingKind.TrailingData);
    }

    [Fact]
    public void Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Finder.Analyze(LayeredPng(out _), cancellationToken: cts.Token));
    }

    [Fact]
    public void Analyses_a_file_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sweethide-forensics-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, LayeredPng(out _));
        try
        {
            var report = Finder.AnalyzeFile(path, Ct);

            Assert.Equal(Path.GetFileName(path), report.FileName);
            Assert.Contains(report.Findings, f => f.Kind == FindingKind.TrailingData);
            Assert.Single(new StegoFinder(new StegoFinderOptions { MaxFileBytes = 100 }).AnalyzeFile(path, Ct).Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Serialises_reports_as_json()
    {
        var report = Finder.Analyze(LayeredPng(out _), "layered.png", Ct);

        var json = JsonSerializer.Serialize(report, ForensicsJsonContext.Default.StegoFinderReport);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("layered.png", root.GetProperty("fileName").GetString());
        var findings = root.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(report.Findings.Count, findings.Count);
        var trailing = findings.Single(f => f.GetProperty("kind").GetString() == "TrailingData");
        Assert.True(trailing.GetProperty("payloadLength").GetInt32() > 0);
        Assert.False(trailing.TryGetProperty("payload", out _));

        var back = JsonSerializer.Deserialize(json, ForensicsJsonContext.Default.StegoFinderReport);
        Assert.NotNull(back);
        Assert.Equal(report.Findings.Select(f => (f.Kind, f.Title, f.Confidence)), back.Findings.Select(f => (f.Kind, f.Title, f.Confidence)));
    }

    [Fact]
    public void Serialises_component_results_including_nan()
    {
        var steganalysis = Steganalyzer.Analyze(Solid(4, 4, 1, 1, 1, a: 0), Ct);
        var container = ContainerInspector.Inspect(Png(8, 8, ("stEg", Utf8("x"))), cancellationToken: Ct);

        var stegJson = JsonSerializer.Serialize(steganalysis, ForensicsJsonContext.Default.SteganalysisReport);
        var containerJson = JsonSerializer.Serialize(container, ForensicsJsonContext.Default.ContainerReport);

        Assert.Contains("\"NaN\"", stegJson, StringComparison.Ordinal);
        Assert.Contains("\"traits\": \"Private, Unknown, Suspicious\"", containerJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var seeds = HostileInputTests.Seeds().Append(LayeredPng(out _)).ToArray();
        var gen =
            from index in Gen.Choose(0, seeds.Length - 1)
            from flips in Gen.ListOf(Gen.Zip(Gen.Choose(0, 4096), Gen.Choose(0, 255)))
            from cut in Gen.Choose(0, 1 << 16)
            select HostileInputTests.Mutate(seeds[index], flips, cut);

        Prop.ForAll(gen.ToArbitrary(), file => Finder.Analyze(file, cancellationToken: Ct).FileSize == file.Length)
            .Check(Config.QuickThrowOnFailure.WithMaxTest(40));
    }
}
