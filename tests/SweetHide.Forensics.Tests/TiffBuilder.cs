using System.Buffers.Binary;
using System.Text;

namespace SweetHide.Forensics.Tests;

/// <summary>Writes a small TIFF/EXIF block (IFD0, Exif IFD, GPS IFD) in either byte order.</summary>
internal sealed class TiffBuilder(bool littleEndian = true)
{
    private readonly List<Entry> _ifd0 = [];
    private readonly List<Entry> _exif = [];
    private readonly List<Entry> _gps = [];

    public TiffBuilder Ascii(string ifd, ushort tag, string text) => Raw(ifd, tag, 2, [.. Encoding.UTF8.GetBytes(text), 0]);

    public TiffBuilder AsciiBytes(string ifd, ushort tag, byte[] bytes) => Raw(ifd, tag, 2, bytes);

    public TiffBuilder Utf16(ushort tag, string text) => Raw("IFD0", tag, 1, [.. Encoding.Unicode.GetBytes(text), 0, 0]);

    public TiffBuilder Undefined(string ifd, ushort tag, byte[] bytes) => Raw(ifd, tag, 7, bytes);

    public TiffBuilder Rationals(string ifd, ushort tag, params (uint Numerator, uint Denominator)[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            U32(bytes.AsSpan(i * 8), values[i].Numerator);
            U32(bytes.AsSpan((i * 8) + 4), values[i].Denominator);
        }

        return Raw(ifd, tag, 5, bytes, (uint)values.Length);
    }

    public TiffBuilder Raw(string ifd, ushort tag, ushort type, byte[] value, uint? count = null)
    {
        var list = ifd switch
        {
            "IFD0" => _ifd0,
            "Exif" => _exif,
            "GPS" => _gps,
            _ => throw new ArgumentOutOfRangeException(nameof(ifd)),
        };
        list.Add(new Entry(tag, type, count ?? (uint)value.Length, value));
        return this;
    }

    public byte[] Build()
    {
        var ifd0 = new List<Entry>(_ifd0);
        if (_exif.Count > 0)
        {
            ifd0.Add(new Entry(0x8769, 4, 1, new byte[4]));
        }

        if (_gps.Count > 0)
        {
            ifd0.Add(new Entry(0x8825, 4, 1, new byte[4]));
        }

        var exifStart = 8 + BlockSize(ifd0);
        var gpsStart = exifStart + (_exif.Count > 0 ? BlockSize(_exif) : 0);
        foreach (var entry in ifd0)
        {
            if (entry.Tag == 0x8769)
            {
                U32(entry.Value, (uint)exifStart);
            }
            else if (entry.Tag == 0x8825)
            {
                U32(entry.Value, (uint)gpsStart);
            }
        }

        using var output = new MemoryStream();
        output.Write(littleEndian ? "II"u8 : "MM"u8);
        var header = new byte[6];
        U16(header, 42);
        U32(header.AsSpan(2), 8);
        output.Write(header);
        output.Write(Block(ifd0, 8));
        if (_exif.Count > 0)
        {
            output.Write(Block(_exif, exifStart));
        }

        if (_gps.Count > 0)
        {
            output.Write(Block(_gps, gpsStart));
        }

        return output.ToArray();
    }

    private static int BlockSize(List<Entry> entries) =>
        2 + (12 * entries.Count) + 4 + entries.Where(e => e.Value.Length > 4).Sum(e => (e.Value.Length + 1) & ~1);

    private byte[] Block(List<Entry> entries, int start)
    {
        var sorted = entries.OrderBy(e => e.Tag).ToList();
        var headerSize = 2 + (12 * sorted.Count) + 4;
        var block = new byte[BlockSize(sorted)];
        U16(block, (ushort)sorted.Count);
        var valueAt = headerSize;
        for (var i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];
            var at = 2 + (12 * i);
            U16(block.AsSpan(at), entry.Tag);
            U16(block.AsSpan(at + 2), entry.Type);
            U32(block.AsSpan(at + 4), entry.Count);
            if (entry.Value.Length <= 4)
            {
                entry.Value.CopyTo(block, at + 8);
            }
            else
            {
                U32(block.AsSpan(at + 8), (uint)(start + valueAt));
                entry.Value.CopyTo(block, valueAt);
                valueAt += (entry.Value.Length + 1) & ~1;
            }
        }

        return block;
    }

    private void U16(Span<byte> target, ushort value)
    {
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(target, value);
        }
    }

    private void U32(Span<byte> target, uint value)
    {
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
        }
    }

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);
}
