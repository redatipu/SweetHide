using SweetHide.Forensics.Detection;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>Cuts byte ranges out of a file (no copy) and names them sensibly.</summary>
public static class Carver
{
    /// <summary>Bytes from <paramref name="offset"/>, for <paramref name="length"/> bytes or to the end of the file.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range does not lie within the file.</exception>
    public static ReadOnlyMemory<byte> Carve(ReadOnlyMemory<byte> file, long offset, long? length = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, file.Length);
        var available = file.Length - offset;
        var count = length ?? available;
        ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(length));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, available, nameof(length));
        return file.Slice((int)offset, (int)count);
    }

    /// <summary>The embedded file, using its estimated length or running to the end of the file.</summary>
    public static ReadOnlyMemory<byte> Carve(ReadOnlyMemory<byte> file, EmbeddedFile embedded)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        return Carve(file, embedded.Offset, embedded.Length);
    }

    /// <summary>Everything after the container's logical end.</summary>
    public static ReadOnlyMemory<byte> Carve(ReadOnlyMemory<byte> file, TrailingData trailing)
    {
        ArgumentNullException.ThrowIfNull(trailing);
        return Carve(file, trailing.Offset, trailing.Length);
    }

    /// <summary>"carved_0x1A2B.zip": the offset keeps names unique, the extension comes from the detected type.</summary>
    public static string SuggestFileName(long offset, FileTypeMatch? type, string prefix = "carved") =>
        Invariant($"{prefix}_0x{offset:X}{type?.Extension ?? ".bin"}");
}
