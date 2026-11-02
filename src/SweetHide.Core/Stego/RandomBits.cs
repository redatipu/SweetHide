using System.Security.Cryptography;

namespace SweetHide.Core.Stego;

/// <summary>Buffered cryptographically secure coin flips (for tie-breaking and padding).</summary>
internal sealed class RandomBits
{
    private readonly byte[] _buffer = new byte[256];
    private int _bitPosition = int.MaxValue;

    public bool Next()
    {
        if (_bitPosition >= _buffer.Length * 8)
        {
            RandomNumberGenerator.Fill(_buffer);
            _bitPosition = 0;
        }

        var bit = (_buffer[_bitPosition >> 3] >> (_bitPosition & 7)) & 1;
        _bitPosition++;
        return bit == 1;
    }
}
