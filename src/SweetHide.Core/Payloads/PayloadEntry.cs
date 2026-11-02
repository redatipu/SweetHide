using System.Text;

namespace SweetHide.Core.Payloads;

public enum PayloadEntryKind : byte
{
    Text = 1,
    File = 2,
}

/// <summary>One item hidden in an image: a text message or a file.</summary>
public sealed class PayloadEntry
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _content;

    internal PayloadEntry(PayloadEntryKind kind, string name, byte[] content)
    {
        Kind = kind;
        Name = name;
        _content = content;
    }

    public PayloadEntryKind Kind { get; }

    /// <summary>File name as supplied by whoever hid it. Untrusted: pass through <see cref="SafeFileName"/> before use.</summary>
    public string Name { get; }

    public ReadOnlyMemory<byte> Content => _content;

    public int Length => _content.Length;

    public static PayloadEntry FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PayloadEntry(PayloadEntryKind.Text, string.Empty, StrictUtf8.GetBytes(text));
    }

    /// <param name="fileName">Only the last path component is kept.</param>
    /// <param name="content">The file's bytes.</param>
    public static PayloadEntry FromFile(string fileName, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var name = SafeFileName.LastComponent(fileName);
        if (name.Length == 0)
        {
            throw new ArgumentException("File name has no name component.", nameof(fileName));
        }

        return new PayloadEntry(PayloadEntryKind.File, name, content.ToArray());
    }

    /// <summary>Decodes a text entry.</summary>
    /// <exception cref="InvalidOperationException">This entry is a file.</exception>
    public string GetText() =>
        Kind == PayloadEntryKind.Text
            ? StrictUtf8.GetString(_content)
            : throw new InvalidOperationException("This entry is a file, not text.");
}
