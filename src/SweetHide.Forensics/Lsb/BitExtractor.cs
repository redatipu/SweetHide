using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Lsb;

/// <summary>Reads one bit plane out of pixels in a given order and packs the bits into bytes.</summary>
internal static class BitExtractor
{
    private const int CancellationCheckInterval = 1 << 16;

    /// <summary>Fills <paramref name="destination"/> from the start of the stream; returns bytes written.</summary>
    public static int Extract(PixelBuffer image, LsbConfig config, Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        ReadOnlySpan<byte> rgba = image.Pixels;
        var offsets = LsbConfig.ChannelOffsets(config.Channels);
        var packer = new BitPacker(destination, config.BitPlane, config.BitOrder == BitOrder.MsbFirst);
        var width = image.Width;
        var height = image.Height;
        var visited = 0;

        if (config.Order == PixelOrder.RowMajor)
        {
            // Row-major is just the buffer in order, which keeps the hot loop simple.
            for (var p = 0; p < rgba.Length; p += 4)
            {
                if (packer.PushPixel(rgba, p, offsets))
                {
                    break;
                }

                if (++visited % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        else
        {
            var stride = width * 4;
            var done = false;
            for (var x = 0; x < width && !done; x++)
            {
                for (var p = x * 4; p < rgba.Length; p += stride)
                {
                    if (packer.PushPixel(rgba, p, offsets))
                    {
                        done = true;
                        break;
                    }
                }

                visited += height;
                if (visited >= CancellationCheckInterval)
                {
                    visited = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        return packer.Written;
    }

    private ref struct BitPacker(Span<byte> destination, int bitPlane, bool msbFirst)
    {
        private readonly Span<byte> _destination = destination;
        private int _accumulator;
        private int _bits;

        public int Written { get; private set; }

        /// <summary>Adds one bit per channel; returns true once the destination is full.</summary>
        public bool PushPixel(ReadOnlySpan<byte> rgba, int pixel, ReadOnlySpan<int> offsets)
        {
            foreach (var offset in offsets)
            {
                var bit = (rgba[pixel + offset] >> bitPlane) & 1;
                _accumulator = msbFirst ? (_accumulator << 1) | bit : _accumulator | (bit << _bits);
                if (++_bits == 8)
                {
                    _destination[Written++] = (byte)_accumulator;
                    _accumulator = 0;
                    _bits = 0;
                    if (Written == _destination.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
