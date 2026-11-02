namespace SweetHide.Forensics.Detection;

/// <summary>Cuts a payload out of an extracted stream according to what <see cref="ContentAnalyzer"/> found.</summary>
internal static class PayloadTrimmer
{
    public static byte[] Trim(ReadOnlySpan<byte> stream, ContentAnalysis content, int maxBytes)
    {
        if (content.PayloadOffset >= stream.Length)
        {
            return [];
        }

        var payload = stream[content.PayloadOffset..];
        if (content.PayloadLength is { } length && length < payload.Length)
        {
            payload = payload[..(int)length];
        }
        else if (content.PayloadLength is null && content.Kind == ContentKind.Text)
        {
            payload = payload[..TextHeuristics.LeadingRun(payload).Bytes];
        }

        return payload[..Math.Min(payload.Length, maxBytes)].ToArray();
    }
}
