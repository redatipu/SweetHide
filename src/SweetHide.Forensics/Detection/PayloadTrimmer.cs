using SweetHide.Forensics.Containers;

namespace SweetHide.Forensics.Detection;

/// <summary>Cuts a payload out of an extracted stream according to what <see cref="ContentAnalyzer"/> found.</summary>
internal static class PayloadTrimmer
{
    public static byte[] Trim(byte[] stream, ContentAnalysis content, int maxBytes)
    {
        if (content.PayloadOffset >= stream.Length)
        {
            return [];
        }

        var payload = stream.AsMemory(content.PayloadOffset);
        if (content.PayloadLength is { } length && length < payload.Length)
        {
            payload = payload[..(int)length];
        }
        else if (content.PayloadLength is null && content.Kind == ContentKind.Text)
        {
            payload = payload[..TextHeuristics.LeadingRun(payload.Span).Bytes];
        }
        else if (content.PayloadLength is null && content.Kind == ContentKind.File && EmbeddedLength.Estimate(content.FileType, payload) is { } fileLength)
        {
            payload = payload[..(int)fileLength];
        }

        return payload[..Math.Min(payload.Length, maxBytes)].ToArray();
    }
}
