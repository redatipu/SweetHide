using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Containers;

/// <summary>binwalk-style scan: finds known file signatures at any offset and estimates each file's length.</summary>
public static class SignatureScanner
{
    private const int CancellationCheckInterval = 1 << 16;

    /// <summary>Signatures in offset order, including the file's own one at offset 0.</summary>
    public static IReadOnlyList<EmbeddedFile> Scan(ReadOnlyMemory<byte> data, int maxResults = 256, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResults);
        var span = data.Span;
        var lengths = new EmbeddedLength(data);
        var results = new List<EmbeddedFile>();
        long zipEnd = -1;
        var offset = 0;
        var checks = 0;
        while (offset < span.Length && results.Count < maxResults)
        {
            var next = span[offset..].IndexOfAny(FileSignatures.FirstBytes);
            if (next < 0)
            {
                break;
            }

            offset += next;
            if (++checks % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (FileSignatures.Identify(span[offset..]) is { } match)
            {
                // Every member of a ZIP has its own local header; report the archive once.
                if (match.Type != FileType.Zip || offset >= zipEnd)
                {
                    var length = lengths.EstimateAt(match.Type, offset);
                    results.Add(new EmbeddedFile(offset, length, match, offset == 0 ? "start of file" : null));
                    if (match.Type == FileType.Zip)
                    {
                        zipEnd = length is { } l ? offset + l : span.Length;
                    }
                }
            }

            offset++;
        }

        return results;
    }
}
