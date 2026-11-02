namespace SweetHide.Forensics.Detection;

/// <summary>File types the forensics tools recognise from their magic bytes.</summary>
public enum FileType
{
    Unknown = 0,
    Png,
    Jpeg,
    Gif,
    WebP,
    Bmp,
    Wav,
    Avi,
    Riff,
    Zip,
    Pdf,
    SevenZip,
    Rar,
    Gzip,
    Bzip2,
    Xz,
    Zstandard,
    Elf,
    PortableExecutable,
    Ogg,
    Flac,
    Mp3,
    Sqlite,
    Pem,
}

/// <summary>A file type recognised from its magic bytes.</summary>
/// <param name="Type">The recognised type.</param>
/// <param name="Name">Human-readable name, e.g. "PNG image".</param>
/// <param name="Extension">Usual extension including the dot, e.g. ".png".</param>
public sealed record FileTypeMatch(FileType Type, string Name, string Extension);
