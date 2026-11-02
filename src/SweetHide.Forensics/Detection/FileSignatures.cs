using System.Buffers;
using System.Buffers.Binary;

namespace SweetHide.Forensics.Detection;

/// <summary>
/// Recognises files from their magic bytes. Short magics (MZ, BM, gzip, ID3, ...) are validated against the header
/// fields that follow, so random data rarely matches.
/// </summary>
public static class FileSignatures
{
    private static readonly Signature[] Table =
    [
        new(FileType.Png, "PNG image", ".png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        new(FileType.Jpeg, "JPEG image", ".jpg", [0xFF, 0xD8, 0xFF], static d => d.Length > 3 && d[3] >= 0xC0 && d[3] != 0xFF),
        new(FileType.Gif, "GIF image", ".gif", "GIF87a"u8.ToArray()),
        new(FileType.Gif, "GIF image", ".gif", "GIF89a"u8.ToArray()),
        new(FileType.WebP, "WebP image", ".webp", "RIFF"u8.ToArray(), static d => RiffForm(d, "WEBP"u8)),
        new(FileType.Wav, "WAV audio", ".wav", "RIFF"u8.ToArray(), static d => RiffForm(d, "WAVE"u8)),
        new(FileType.Avi, "AVI video", ".avi", "RIFF"u8.ToArray(), static d => RiffForm(d, "AVI "u8)),
        new(FileType.Riff, "RIFF container", ".riff", "RIFF"u8.ToArray(), IsOtherRiff),
        new(FileType.Bmp, "BMP image", ".bmp", "BM"u8.ToArray(), IsBmp),
        new(FileType.Zip, "ZIP archive", ".zip", [0x50, 0x4B, 0x03, 0x04]),
        new(FileType.Pdf, "PDF document", ".pdf", "%PDF-"u8.ToArray()),
        new(FileType.SevenZip, "7-Zip archive", ".7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
        new(FileType.Rar, "RAR archive", ".rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], static d => d.Length > 6 && (d[6] == 0 || (d[6] == 1 && d.Length > 7 && d[7] == 0))),
        new(FileType.Gzip, "gzip data", ".gz", [0x1F, 0x8B, 0x08], IsGzip),
        new(FileType.Bzip2, "bzip2 data", ".bz2", "BZh"u8.ToArray(), static d => d.Length >= 10 && d[3] is >= (byte)'1' and <= (byte)'9' && d[4..10].SequenceEqual((ReadOnlySpan<byte>)[0x31, 0x41, 0x59, 0x26, 0x53, 0x59])),
        new(FileType.Xz, "xz data", ".xz", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),
        new(FileType.Zstandard, "Zstandard data", ".zst", [0x28, 0xB5, 0x2F, 0xFD]),
        new(FileType.Elf, "ELF executable", ".elf", [0x7F, 0x45, 0x4C, 0x46], static d => d.Length > 6 && d[4] is 1 or 2 && d[5] is 1 or 2 && d[6] == 1),
        new(FileType.PortableExecutable, "Windows executable (PE)", ".exe", "MZ"u8.ToArray(), IsPe),
        new(FileType.Ogg, "Ogg media", ".ogg", [0x4F, 0x67, 0x67, 0x53, 0x00]),
        new(FileType.Flac, "FLAC audio", ".flac", "fLaC"u8.ToArray()),
        new(FileType.Mp3, "MP3 audio (ID3)", ".mp3", "ID3"u8.ToArray(), static d => d.Length >= 10 && d[3] is >= 2 and <= 4 && d[4] != 0xFF && (d[6] | d[7] | d[8] | d[9]) < 0x80),
        new(FileType.Sqlite, "SQLite database", ".sqlite", "SQLite format 3\0"u8.ToArray()),
        new(FileType.Pem, "PEM block (key or certificate)", ".pem", "-----BEGIN "u8.ToArray()),
    ];

    private static readonly SearchValues<byte> FirstBytesValues = SearchValues.Create([.. Table.Select(s => s.Magic[0]).Distinct()]);

    /// <summary>Every type that <see cref="Identify"/> can return.</summary>
    public static IReadOnlyList<FileTypeMatch> KnownTypes { get; } = [.. Table.Select(s => s.Match).DistinctBy(m => m.Type)];

    /// <summary>Bytes that can start a signature; lets a scanner skip ahead with a vectorised search.</summary>
    internal static SearchValues<byte> FirstBytes => FirstBytesValues;

    /// <summary>Returns the type of a file that starts at the beginning of <paramref name="data"/>, if recognised.</summary>
    public static FileTypeMatch? Identify(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || !FirstBytesValues.Contains(data[0]))
        {
            return null;
        }

        foreach (var signature in Table)
        {
            if (data.StartsWith(signature.Magic) && (signature.Validate is null || signature.Validate(data)))
            {
                return signature.Match;
            }
        }

        return null;
    }

    internal static FileTypeMatch MatchFor(FileType type) =>
        KnownTypes.FirstOrDefault(m => m.Type == type) ?? new FileTypeMatch(type, type.ToString(), ".bin");

    private static bool RiffForm(ReadOnlySpan<byte> d, ReadOnlySpan<byte> form) => d.Length >= 12 && d.Slice(8, 4).SequenceEqual(form);

    private static bool IsOtherRiff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || RiffForm(d, "WEBP"u8) || RiffForm(d, "WAVE"u8) || RiffForm(d, "AVI "u8))
        {
            return false;
        }

        foreach (var c in d.Slice(8, 4))
        {
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBmp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18)
        {
            return false;
        }

        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(d[2..]);
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(d[6..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[10..]);
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(d[14..]);
        return reserved == 0
            && fileSize >= 26
            && pixelOffset >= 26 && pixelOffset <= fileSize
            && dibSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    private static bool IsGzip(ReadOnlySpan<byte> d) =>
        d.Length >= 10 && (d[3] & 0xE0) == 0 && d[8] is 0 or 2 or 4 && (d[9] <= 13 || d[9] == 255);

    private static bool IsPe(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x40)
        {
            return false;
        }

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[0x3C..]);
        return peOffset >= 0x40 && peOffset <= 0x10000 && peOffset + 4 <= (uint)d.Length && d.Slice((int)peOffset, 4).SequenceEqual("PE\0\0"u8);
    }

    private delegate bool SignatureValidator(ReadOnlySpan<byte> data);

    private sealed class Signature(FileType type, string name, string extension, byte[] magic, SignatureValidator? validate = null)
    {
        public byte[] Magic { get; } = magic;

        public SignatureValidator? Validate { get; } = validate;

        public FileTypeMatch Match { get; } = new(type, name, extension);
    }
}
