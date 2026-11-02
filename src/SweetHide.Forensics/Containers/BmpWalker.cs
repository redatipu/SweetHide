using System.Buffers.Binary;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>
/// Lists the BMP headers, colour table and pixel array. The logical end is the end of the pixel array (not the size
/// field, which tools can inflate to cover appended data), and a gap before the pixels is flagged.
/// </summary>
internal static class BmpWalker
{
    private const int FileHeaderSize = 14;
    private const int GapTolerance = 16;

    public static WalkResult Walk(ReadOnlySpan<byte> data, ContainerInspectorOptions options)
    {
        var result = new WalkResult(options.MaxStructureItems, data.Length);
        if (data.Length < FileHeaderSize + 4 || data[0] != 'B' || data[1] != 'M')
        {
            result.Warn("Missing BMP header.");
            return result;
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        result.Add(new StructureItem("File header", 0, FileHeaderSize, 2, 12, Invariant($"declares {declaredSize} bytes, pixels at 0x{pixelOffset:X}"), StructureTraits.None));

        if (dibSize is < 12 or > 1024 || FileHeaderSize + dibSize > data.Length)
        {
            result.Warn(Invariant($"Implausible DIB header size {dibSize}."));
            return result;
        }

        long width;
        long height;
        int bitsPerPixel;
        uint compression = 0;
        uint imageSize = 0;
        uint coloursUsed = 0;
        var dib = data.Slice(FileHeaderSize, (int)dibSize);
        if (dibSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
        }
        else if (dibSize >= 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
            height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(dib[8..]));
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            imageSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[20..]);
            coloursUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        }
        else
        {
            result.Warn(Invariant($"Unsupported DIB header size {dibSize}."));
            return result;
        }

        result.Add(new StructureItem("DIB header", FileHeaderSize, dibSize, FileHeaderSize, dibSize, Invariant($"{width}x{height}, {bitsPerPixel} bpp, compression {compression}"), StructureTraits.None));

        long tableEnd = FileHeaderSize + dibSize;
        if (dibSize == 40 && compression is 3 or 6)
        {
            tableEnd += compression == 3 ? 12 : 16; // BI_BITFIELDS / BI_ALPHABITFIELDS masks follow the header
        }

        if (bitsPerPixel is > 0 and <= 8)
        {
            var entries = coloursUsed is > 0 and <= 256 ? coloursUsed : 1u << bitsPerPixel;
            var tableBytes = entries * (dibSize == 12 ? 3 : 4);
            result.Add(new StructureItem("Colour table", tableEnd, tableBytes, tableEnd, tableBytes, Invariant($"{entries} colours"), StructureTraits.None));
            tableEnd += tableBytes;
        }

        if (pixelOffset > tableEnd)
        {
            var gap = pixelOffset - tableEnd;
            var flags = gap > GapTolerance ? StructureTraits.Suspicious : StructureTraits.None;
            result.Add(new StructureItem("Gap", tableEnd, gap, tableEnd, gap, Invariant($"{gap} unused bytes before the pixel array"), flags));
        }

        // Once both factors are known to be below 2^31, the pixel array size below cannot overflow.
        var rowBytes = ((width * bitsPerPixel) + 31) / 32 * 4;
        if (width <= 0 || height <= 0 || bitsPerPixel is 0 or > 64 || rowBytes > data.Length || height > data.Length)
        {
            result.Warn("Implausible BMP dimensions.");
            return result;
        }

        var pixelBytes = compression is 0 or 3 or 6 ? rowBytes * height : imageSize;
        var pixelEnd = pixelOffset + pixelBytes;
        if (pixelBytes <= 0 || pixelEnd > data.Length)
        {
            result.Warn(Invariant($"The pixel array ({pixelBytes} bytes at 0x{pixelOffset:X}) does not fit in the file."));
            return result;
        }

        result.Add(new StructureItem("Pixel array", pixelOffset, pixelBytes, pixelOffset, pixelBytes, Invariant($"{pixelBytes} bytes"), StructureTraits.None));
        if (declaredSize != pixelEnd)
        {
            result.Warn(Invariant($"The header declares {declaredSize} bytes but the pixel array ends at {pixelEnd}."));
        }

        result.End = pixelEnd;
        return result;
    }

    public static long? MeasureLength(ReadOnlySpan<byte> data) => Walk(data, new ContainerInspectorOptions { MaxStructureItems = 0 }).End;
}
