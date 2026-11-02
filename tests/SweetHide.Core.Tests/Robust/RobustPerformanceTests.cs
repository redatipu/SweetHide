using System.Diagnostics;

using SweetHide.Core.Robust;

namespace SweetHide.Core.Tests.Robust;

/// <summary>Separate class so it runs in parallel with the other robust-mode tests.</summary>
public class RobustPerformanceTests
{
    [Fact]
    public void A_12_megapixel_photo_is_fast_and_the_marking_is_faint()
    {
        var clock = Stopwatch.StartNew();
        var photo = RobustTestImages.Photo(4000, 3000, seed: 11);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"generated the 12 MP cover in {clock.ElapsedMilliseconds} ms");
        foreach (var profile in RobustProfile.All)
        {
            var payload = RobustTestImages.Payload(RobustStego.MaxPayloadBytes(photo.Width, photo.Height, profile));
            clock.Restart();
            var prepared = RobustStego.PrepareCover(photo, profile);
            var marked = RobustStego.Embed(prepared, payload, RobustTestImages.Key(1), profile); // already at the safe size: no second resize
            var embedTime = clock.Elapsed;
            var sent = RobustStegoTests.SendAsJpeg(marked, profile);
            var received = RobustTestImages.Recompress(sent, 70);
            clock.Restart();
            var result = RobustStego.Extract(received, RobustTestImages.Key(1), profile);
            var extractTime = clock.Elapsed;

            var markingPsnr = ImageMetrics.Compare(prepared, marked).Psnr;
            var sentPsnr = ImageMetrics.Compare(prepared, sent).Psnr;
            output?.WriteLine(
                $"{profile.Name}: {marked.Width}x{marked.Height}, capacity {payload.Length} B, PSNR marking {markingPsnr:F1} dB, " +
                $"as sent (q{profile.OutputJpegQuality}) {sentPsnr:F1} dB, embed {embedTime.TotalMilliseconds:F0} ms, extract {extractTime.TotalMilliseconds:F0} ms");

            RobustStegoTests.AssertFound(result, payload);
            Assert.True(markingPsnr > 44, $"marking PSNR {markingPsnr:F1} dB");
            Assert.True(embedTime + extractTime < TimeSpan.FromSeconds(10), $"took {embedTime + extractTime}");
        }
    }
}
