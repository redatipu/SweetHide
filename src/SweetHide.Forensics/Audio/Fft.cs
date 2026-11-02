namespace SweetHide.Forensics.Audio;

/// <summary>In-place iterative radix-2 Cooley–Tukey FFT.</summary>
internal sealed class Fft
{
    private readonly int[] _reversed;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size must be a power of two.");
        }

        Size = size;
        var bits = int.Log2(size);
        _reversed = new int[size];
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reversed[i] = r;
        }

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = Math.Sin(-2 * Math.PI * i / size);
        }
    }

    public int Size { get; }

    public void Transform(Span<double> real, Span<double> imaginary)
    {
        for (var i = 0; i < Size; i++)
        {
            var j = _reversed[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= Size; length <<= 1)
        {
            var half = length / 2;
            var step = Size / length;
            for (var start = 0; start < Size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = (real[b] * wr) - (imaginary[b] * wi);
                    var ti = (real[b] * wi) + (imaginary[b] * wr);
                    real[b] = real[a] - tr;
                    imaginary[b] = imaginary[a] - ti;
                    real[a] += tr;
                    imaginary[a] += ti;
                }
            }
        }
    }
}
