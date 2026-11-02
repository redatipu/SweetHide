using System.Globalization;
using System.Text;
using System.Text.Unicode;

using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Metadata;

/// <summary>Collects fields, applying the length cap and the "unusual" rules in one place.</summary>
internal sealed class FieldCollector(MetadataOptions options)
{
    public List<MetadataField> Fields { get; } = [];

    public MetadataOptions Options { get; } = options;

    public bool IsFull => Fields.Count >= Options.MaxFields;

    /// <summary>Decompressed bytes still allowed in this read.</summary>
    public long DecompressionBudget { get; set; } = options.MaxTotalDecompressedBytes;

    /// <summary>Adds a text value decoded from raw bytes (UTF-8 when valid, Latin-1 otherwise).</summary>
    public void AddBytes(string source, string name, ReadOnlySpan<byte> raw, long offset, string? note = null, bool unusual = false) =>
        AddText(source, name, Decode(raw), offset, raw.Length, note, unusual, binary: TextHeuristics.TextRatio(raw) < 0.9);

    /// <param name="source">Where the field lives.</param>
    /// <param name="name">Field name.</param>
    /// <param name="value">Decoded text.</param>
    /// <param name="offset">File offset of the raw value.</param>
    /// <param name="length">Raw length in bytes.</param>
    /// <param name="note">Remark shown with the field.</param>
    /// <param name="unusual">The caller already knows the field is unusual (the note says why).</param>
    /// <param name="binary">The raw value is not text.</param>
    /// <param name="isXmp">Use the (higher) XMP length threshold.</param>
    public void AddText(string source, string name, string value, long offset, int length, string? note = null, bool unusual = false, bool binary = false, bool isXmp = false)
    {
        if (IsFull)
        {
            return;
        }

        var notes = new List<string>(2);
        if (note is not null)
        {
            notes.Add(note);
        }

        var threshold = isXmp ? Options.LongXmpThreshold : Options.LongFieldThreshold;
        if (value.Length > threshold)
        {
            unusual = true;
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"unusually long ({value.Length} characters)"));
        }

        if (binary && value.Length > 0)
        {
            unusual = true;
            notes.Add("contains non-text bytes");
        }

        if (value.Length > Options.MaxValueChars)
        {
            var cut = Options.MaxValueChars;
            if (char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }

            value = value[..cut] + "…";
            notes.Add("value shortened for display");
        }

        var text = notes.Count == 0 ? null : string.Join("; ", notes);
        Fields.Add(new MetadataField(source, name, value, offset, length, unusual, text));
    }

    /// <summary>UTF-8 when the bytes are valid UTF-8, otherwise Latin-1 (so no byte is ever lost).</summary>
    public static string Decode(ReadOnlySpan<byte> raw) => Utf8.IsValid(raw) ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);
}
