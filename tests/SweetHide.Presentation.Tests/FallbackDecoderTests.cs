using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public class FallbackDecoderTests
{
    [Fact]
    public async Task Formats_skia_cannot_read_go_to_the_fallback()
    {
        var fake = new FakeHeicDecoder();
        var loader = new ImageLoader(Codec, fake);

        var image = await loader.LoadBytesAsync("IMG_0001.HEIC", "ftypheic-not-really"u8.ToArray(), ImagePurpose.Cover);

        Assert.Equal(4, image.Width);
        Assert.True(fake.AppliedOrientation);
        Assert.True(image.CapacityFor(1) == 0); // 4x2 is too small to hide anything, but it loaded

        var png = await loader.LoadBytesAsync("normal.png", PngBytes(20, 20), ImagePurpose.Inspect);
        Assert.Equal(20, png.Width);
        Assert.Equal(1, fake.Calls); // Skia handled the PNG itself
    }

    [Fact]
    public async Task Unreadable_by_both_is_still_an_image_error()
    {
        var loader = new ImageLoader(Codec, new NothingDecoder());
        await Assert.ThrowsAsync<InvalidImageException>(() => loader.LoadBytesAsync("x.heic", [1, 2, 3], ImagePurpose.Cover));
    }

    private sealed class FakeHeicDecoder : IFallbackImageDecoder
    {
        public int Calls { get; private set; }

        public bool AppliedOrientation { get; private set; }

        public Task<PixelBuffer?> TryDecodeAsync(byte[] bytes, bool applyOrientation)
        {
            Calls++;
            AppliedOrientation = applyOrientation;
            var data = new byte[4 * 2 * 4];
            Array.Fill(data, (byte)200);
            return Task.FromResult<PixelBuffer?>(new PixelBuffer(4, 2, data));
        }
    }

    private sealed class NothingDecoder : IFallbackImageDecoder
    {
        public Task<PixelBuffer?> TryDecodeAsync(byte[] bytes, bool applyOrientation) => Task.FromResult<PixelBuffer?>(null);
    }
}
