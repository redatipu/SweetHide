using SweetHide.Core.Imaging;

namespace SweetHide.Core.Jpeg;

/// <summary>
/// Reads entropy-coded data: removes byte stuffing (FF 00), stops at markers and refuses to decode past them.
/// </summary>
internal struct JpegBitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private ulong _buffer; // The low _count bits are pending, most significant first.
    private int _count;
    private int _fakeBits; // Zero bits appended after a marker or the end of data; consuming them means truncation.
    private bool _atMarker;

    public JpegBitReader(byte[] data, int position, int end)
    {
        _data = data;
        _position = position;
        _end = end;
    }

    /// <summary>Offset of the first byte not yet loaded (at or before the next marker).</summary>
    public readonly int Position => _position;

    public int Peek16()
    {
        if (_count < 16)
        {
            Fill();
        }

        return (int)((_buffer >> (_count - 16)) & 0xFFFF);
    }

    public void Skip(int bits)
    {
        _count -= bits;
        if (_count < _fakeBits)
        {
            throw new InvalidImageException("Corrupt JPEG: the entropy-coded data ends too early.");
        }
    }

    public int ReadBits(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        if (_count < bits)
        {
            Fill();
        }

        var value = (int)((_buffer >> (_count - bits)) & ((1UL << bits) - 1));
        Skip(bits);
        return value;
    }

    /// <summary>Discards the padding before a restart marker, then consumes RSTn (n = <paramref name="index"/> mod 8).</summary>
    public void Restart(int index)
    {
        _buffer = 0;
        _count = 0;
        _fakeBits = 0;
        _atMarker = false;

        // Anything between the padding and the marker is garbage; skip it as libjpeg does.
        while (_position + 1 < _end && !(_data[_position] == 0xFF && _data[_position + 1] != 0x00 && _data[_position + 1] != 0xFF))
        {
            _position++;
        }

        if (_position + 1 >= _end || _data[_position + 1] != 0xD0 + (index & 7))
        {
            throw new InvalidImageException("Corrupt JPEG: missing or out-of-order restart marker.");
        }

        _position += 2;
    }

    private void Fill()
    {
        while (_count <= 56)
        {
            uint value = 0;
            if (_atMarker || _position >= _end)
            {
                _fakeBits += 8;
            }
            else if (_data[_position] != 0xFF)
            {
                value = _data[_position++];
            }
            else if (_position + 1 < _end && _data[_position + 1] == 0x00)
            {
                value = 0xFF;
                _position += 2;
            }
            else
            {
                _atMarker = true; // Leave _position on the marker.
                _fakeBits += 8;
            }

            _buffer = (_buffer << 8) | value;
            _count += 8;
        }
    }
}

/// <summary>Writes entropy-coded data with byte stuffing, plus raw marker segments, to a stream.</summary>
internal sealed class JpegBitWriter(Stream output)
{
    private readonly byte[] _chunk = new byte[64 * 1024];
    private int _length;
    private ulong _buffer;
    private int _count;

    public void WriteBits(int bits, int count)
    {
        _buffer = (_buffer << count) | ((uint)bits & ((1UL << count) - 1));
        _count += count;
        while (_count >= 8)
        {
            _count -= 8;
            var b = (byte)(_buffer >> _count);
            Put(b);
            if (b == 0xFF)
            {
                Put(0x00);
            }
        }
    }

    /// <summary>Pads the last partial byte with 1 bits, as T.81 requires before a marker.</summary>
    public void AlignWithOnes()
    {
        if (_count > 0)
        {
            WriteBits((1 << (8 - _count)) - 1, 8 - _count);
        }
    }

    public void WriteMarker(byte marker)
    {
        AlignWithOnes();
        Put(0xFF);
        Put(marker);
    }

    public void WriteByte(int value) => Put((byte)value);

    public void WriteUInt16(int value)
    {
        Put((byte)(value >> 8));
        Put((byte)value);
    }

    public void Flush()
    {
        output.Write(_chunk, 0, _length);
        _length = 0;
    }

    private void Put(byte b)
    {
        if (_length == _chunk.Length)
        {
            Flush();
        }

        _chunk[_length++] = b;
    }
}
