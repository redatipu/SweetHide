using SweetHide.Core;
using SweetHide.Core.Payloads;

namespace SweetHide.Presentation.ViewModels;

/// <summary>One revealed message or file.</summary>
public sealed class RevealedItemViewModel
{
    private const int MaxPreviewBytes = 64 * 1024;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".json", ".log", ".xml", ".yaml", ".yml", ".ini", ".conf", ".cfg", ".srt"];

    internal RevealedItemViewModel(PayloadEntry entry, int messageNumber)
    {
        IsText = entry.Kind == PayloadEntryKind.Text;
        Content = entry.Content;
        Text = IsText ? entry.GetText() : string.Empty;
        FileName = IsText ? string.Empty : SafeFileName.Sanitize(entry.Name);
        Title = IsText ? Strings.Format(Strings.MessageItemFormat, messageNumber) : FileName;
        SizeText = ByteSize.Format(entry.Length);

        var extension = Path.GetExtension(FileName);
        IsImagePreview = !IsText && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        PreviewText = !IsText && TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? TextPreview(entry.Content.Span) : string.Empty;
    }

    /// <summary>The file is an image the UI may preview (decoded in-app, never opened externally).</summary>
    public bool IsImagePreview { get; }

    /// <summary>The start of a text file, or empty when it isn't valid UTF-8 text.</summary>
    public string PreviewText { get; }

    public bool HasTextPreview => PreviewText.Length > 0;

    /// <summary>Bytes for the in-app image preview (empty unless <see cref="IsImagePreview"/>).</summary>
    public byte[] PreviewImageBytes => IsImagePreview ? Content.ToArray() : [];

    public bool IsText { get; }

    public bool IsFile => !IsText;

    public string Title { get; }

    /// <summary>Sanitized file name (empty for messages).</summary>
    public string FileName { get; }

    public string Text { get; }

    public string SizeText { get; }

    public ReadOnlyMemory<byte> Content { get; }

    private static string TextPreview(ReadOnlySpan<byte> content)
    {
        var slice = content.Length > MaxPreviewBytes ? content[..MaxPreviewBytes] : content;
        try
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(slice);
            return content.Length > MaxPreviewBytes ? text + "\n…" : text;
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Cut mid-character, or not text at all: no preview.
            return string.Empty;
        }
    }
}
