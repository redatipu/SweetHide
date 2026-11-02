using System.Security.Cryptography;

namespace SweetHide.Core.Stego;

/// <summary>Where the writer's randomness comes from: salts, keys, padding and LSB-matching coin flips.</summary>
/// <remarks>
/// Production code always uses <see cref="SystemRandomSource"/>. Tests substitute a seeded source to produce
/// byte-identical images, which pins the writer's layout in known-answer tests. Only called from the thread that
/// runs the embed, never from parallel workers, so the output does not depend on scheduling.
/// </remarks>
internal interface IRandomSource
{
    void Fill(Span<byte> destination);
}

/// <summary>The operating system's CSPRNG.</summary>
internal sealed class SystemRandomSource : IRandomSource
{
    public static SystemRandomSource Instance { get; } = new();

    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

/// <summary>Buffered coin flips from an <see cref="IRandomSource"/> (for tie-breaking and padding).</summary>
internal sealed class RandomBits(IRandomSource source)
{
    private readonly byte[] _buffer = new byte[256];
    private int _bitPosition = int.MaxValue;

    public RandomBits()
        : this(SystemRandomSource.Instance)
    {
    }

    public bool Next()
    {
        if (_bitPosition >= _buffer.Length * 8)
        {
            source.Fill(_buffer);
            _bitPosition = 0;
        }

        var bit = (_buffer[_bitPosition >> 3] >> (_bitPosition & 7)) & 1;
        _bitPosition++;
        return bit == 1;
    }
}
