using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Steganalysis;

/// <param name="BlockSize">Block edge in pixels.</param>
/// <param name="Columns">Blocks across.</param>
/// <param name="Rows">Blocks down.</param>
/// <param name="Randomness">
/// Per block, row-major: how random the R/G/B least significant bits look (0 = constant or patterned, 1 = coin flips).
/// </param>
/// <param name="ExcessOverBit1">
/// Per block: LSB randomness minus bit-1 randomness, clamped at 0. Natural texture randomises both bits; embedding
/// randomises only the LSB, so high values point at hidden data in otherwise smooth areas.
/// </param>
/// <param name="SuspiciousBlockRatio">Share of blocks whose excess is above 0.5.</param>
/// <param name="Heatmap">Image-sized: each block coloured from blue (structured LSBs) to red (random LSBs).</param>
public sealed record LsbRandomnessMap(
    int BlockSize,
    int Columns,
    int Rows,
    IReadOnlyList<double> Randomness,
    IReadOnlyList<double> ExcessOverBit1,
    double SuspiciousBlockRatio,
    PixelBuffer Heatmap);

/// <summary>Maps where in the image the least significant bits look random.</summary>
public static class LsbRandomness
{
    private const double SuspiciousExcess = 0.5;

    public static LsbRandomnessMap Map(PixelBuffer image, int blockSize = 32, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 2);

        var columns = (image.Width + blockSize - 1) / blockSize;
        var rows = (image.Height + blockSize - 1) / blockSize;
        var randomness = new double[columns * rows];
        var excess = new double[columns * rows];
        ReadOnlySpan<byte> data = image.Pixels;
        var suspicious = 0;
        for (var by = 0; by < rows; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var bx = 0; bx < columns; bx++)
            {
                var lsb = Measure(data, image.Width, image.Height, bx * blockSize, by * blockSize, blockSize, 0);
                var bit1 = Measure(data, image.Width, image.Height, bx * blockSize, by * blockSize, blockSize, 1);
                var index = (by * columns) + bx;
                randomness[index] = lsb;
                excess[index] = Math.Max(0, lsb - bit1);
                if (excess[index] > SuspiciousExcess)
                {
                    suspicious++;
                }
            }
        }

        return new LsbRandomnessMap(blockSize, columns, rows, randomness, excess, (double)suspicious / randomness.Length, Render(image, blockSize, columns, randomness));
    }

    /// <summary>
    /// Randomness of one bit plane in one block: balance of ones and zeros times the balance of equal and unequal
    /// horizontal neighbours. Both are 1/2 for coin flips; flat areas and stripes push one of them towards 0 or 1.
    /// </summary>
    private static double Measure(ReadOnlySpan<byte> data, int width, int height, int x0, int y0, int size, int bit)
    {
        long ones = 0, samples = 0, equal = 0, neighbours = 0;
        var x1 = Math.Min(width, x0 + size);
        var y1 = Math.Min(height, y0 + size);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * width) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var b = (data[i + c] >> bit) & 1;
                    ones += b;
                    samples++;
                    if (x + 1 < x1)
                    {
                        neighbours++;
                        if (b == ((data[i + 4 + c] >> bit) & 1))
                        {
                            equal++;
                        }
                    }
                }
            }
        }

        if (samples == 0 || neighbours == 0)
        {
            return 0;
        }

        var balance = 1 - Math.Abs((2.0 * ones / samples) - 1);
        var independence = 1 - Math.Abs((2.0 * equal / neighbours) - 1);
        return balance * independence;
    }

    private static PixelBuffer Render(PixelBuffer image, int blockSize, int columns, double[] values)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var v = values[((y / blockSize) * columns) + (x / blockSize)];
                var i = ((y * image.Width) + x) * 4;
                pixels[i] = (byte)(255 * v);
                pixels[i + 1] = (byte)(64 * (1 - Math.Abs((2 * v) - 1)));
                pixels[i + 2] = (byte)(255 * (1 - v));
                pixels[i + 3] = 255;
            }
        }

        return new PixelBuffer(image.Width, image.Height, pixels);
    }
}
