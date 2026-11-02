using SkiaSharp;

using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class CoverAnalyzerTests
{
    [Fact]
    public void Noisy_photo_scores_good()
    {
        var assessment = CoverAnalyzer.Assess(TestImages.Photo(200, 150));
        Assert.Equal(CoverRating.Good, assessment.Rating);
        Assert.InRange(assessment.Score, 85, 100);
        Assert.Empty(assessment.Issues);
        Assert.StartsWith("Cover score", assessment.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Flat_image_scores_poor_with_flat_and_low_texture_issues()
    {
        var assessment = CoverAnalyzer.Assess(TestImages.Solid(100, 80, 30, 120, 200));
        Assert.Equal(0, assessment.Score);
        Assert.Equal(CoverRating.Poor, assessment.Rating);
        Assert.Equal(1.0, assessment.FlatShare);
        Assert.Contains(CoverIssue.FlatAreas, assessment.Issues);
        Assert.Contains(CoverIssue.LowTexture, assessment.Issues);
        Assert.Contains("flat", assessment.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Screenshot_with_a_few_photos_scores_lower_than_a_photo()
    {
        var image = TestImages.Solid(200, 150, 245, 245, 245);
        var photo = TestImages.Photo(80, 60, seed: 3);
        for (var y = 0; y < 60; y++)
        {
            photo.Pixels.Slice(y * 80 * 4, 80 * 4).CopyTo(image.Pixels.Slice(((y + 20) * 200 * 4) + (20 * 4), 80 * 4));
        }

        var assessment = CoverAnalyzer.Assess(image);
        Assert.InRange(assessment.FlatShare, 0.75, 0.9);
        Assert.Equal(CoverRating.Poor, assessment.Rating);
        Assert.Contains(CoverIssue.FlatAreas, assessment.Issues);
    }

    [Fact]
    public void Blown_highlights_are_flagged_as_saturated()
    {
        var image = TestImages.Photo(120, 90);
        for (var i = 0; i < image.Pixels.Length / 2; i++)
        {
            if (i % 4 != 3)
            {
                image.Pixels[i] = 255;
            }
        }

        var assessment = CoverAnalyzer.Assess(image);
        Assert.InRange(assessment.SaturatedShare, 0.45, 0.55);
        Assert.Contains(CoverIssue.SaturatedValues, assessment.Issues);
        Assert.True(assessment.Score < CoverAnalyzer.Assess(TestImages.Photo(120, 90)).Score - 40);
    }

    [Fact]
    public void Decoded_jpeg_is_flagged()
    {
        var photo = TestImages.Photo(256, 192, seed: 9);
        using var bitmap = new SKBitmap(new SKImageInfo(256, 192, SKColorType.Rgba8888, SKAlphaType.Opaque));
        photo.Pixels.CopyTo(bitmap.GetPixelSpan());
        using var jpeg = new MemoryStream();
        bitmap.Encode(jpeg, SKEncodedImageFormat.Jpeg, 60);
        jpeg.Position = 0;
        var decoded = new SkiaImageCodec().Decode(jpeg, applyOrientation: true);

        var assessment = CoverAnalyzer.Assess(decoded);
        Assert.True(assessment.Blockiness >= 1.15, $"blockiness {assessment.Blockiness:F2}");
        Assert.Contains(CoverIssue.JpegOrigin, assessment.Issues);
        Assert.InRange(CoverAnalyzer.Assess(photo).Blockiness, 0.9, 1.1);
    }

    [Fact]
    public void Transparent_images_are_handled()
    {
        var assessment = CoverAnalyzer.Assess(TestImages.Solid(40, 40, 1, 2, 3, a: 0));
        Assert.Equal(0, assessment.Score);
        Assert.Equal([CoverIssue.MostlyTransparent], assessment.Issues);
    }

    [Fact]
    public void Recommends_the_smallest_bits_that_fit()
    {
        var cover = TestImages.Photo(60, 50);
        static Payload Sized(long bytes)
        {
            var content = new byte[bytes];
            new Random(1).NextBytes(content);
            return new Payload([PayloadEntry.FromFile("f", content)]);
        }

        var capacity1 = StegoEngine.GetCapacity(cover, 1).PayloadBytes;
        var capacity2 = StegoEngine.GetCapacity(cover, 2).PayloadBytes;
        var capacity4 = StegoEngine.GetCapacity(cover, 4).PayloadBytes;
        Assert.Equal(1, StegoEngine.RecommendBitsPerChannel(cover, Sized(capacity1 - 100)));
        Assert.Equal(2, StegoEngine.RecommendBitsPerChannel(cover, Sized(capacity1 + 100)));
        Assert.Equal(3, StegoEngine.RecommendBitsPerChannel(cover, Sized(capacity2 + 100)));
        Assert.Null(StegoEngine.RecommendBitsPerChannel(cover, Sized(capacity4 + 100)));

        // Adaptive embedding holds about half, so it needs more bits for the same payload.
        Assert.Equal(2, StegoEngine.RecommendBitsPerChannel(cover, Sized(capacity1 - 100), new EmbedOptions { Adaptive = true }));
    }
}
