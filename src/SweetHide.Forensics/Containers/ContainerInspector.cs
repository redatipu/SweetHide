using SweetHide.Forensics.Detection;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>
/// Detects the container format, lists its structure, finds data after its logical end and scans for embedded files.
/// Strictly read-only and bounds-checked: hostile length fields are reported, never trusted.
/// </summary>
public static class ContainerInspector
{
    private const int TrailingSampleBytes = 4096;

    public static ContainerReport Inspect(ReadOnlyMemory<byte> file, ContainerInspectorOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= ContainerInspectorOptions.Default;
        var data = file.Span;
        var format = FileSignatures.Identify(data);
        var walk = Walk(data, format?.Type ?? FileType.Unknown, options);
        cancellationToken.ThrowIfCancellationRequested();

        TrailingData? trailing = null;
        if (walk.End is { } end && end < data.Length)
        {
            var rest = data[(int)end..];
            trailing = new TrailingData(end, rest.Length, ContentAnalyzer.Analyze(rest[..Math.Min(rest.Length, TrailingSampleBytes)], rest.Length));
        }

        IReadOnlyList<EmbeddedFile> embedded = [];
        if (options.ScanSignatures)
        {
            embedded = [.. SignatureScanner.Scan(file, options.MaxEmbeddedFiles, cancellationToken)
                .Select(e => e with { Location = e.Location ?? Locate(e.Offset, walk.Items, trailing) })];
        }

        return new ContainerReport(format, data.Length, walk.Items, trailing, embedded, walk.Warnings, walk.CorrectedDimensions);
    }

    internal static WalkResult Walk(ReadOnlySpan<byte> data, FileType type, ContainerInspectorOptions options) => type switch
    {
        FileType.Png => PngWalker.Walk(data, options),
        FileType.Jpeg => JpegWalker.Walk(data, options),
        FileType.Gif => GifWalker.Walk(data, options),
        FileType.WebP or FileType.Wav or FileType.Avi or FileType.Riff => RiffWalker.Walk(data, options),
        FileType.Bmp => BmpWalker.Walk(data, options),
        _ => WalkResult.Empty,
    };

    private static string? Locate(long offset, List<StructureItem> items, TrailingData? trailing)
    {
        if (trailing is not null && offset >= trailing.Offset)
        {
            return "trailing data";
        }

        // Items are in file order; find the last one starting at or before the offset.
        int lo = 0, hi = items.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (items[mid].Offset <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 && offset < items[found].Offset + items[found].Length
            ? Invariant($"{items[found].Type} at 0x{items[found].Offset:X}")
            : null;
    }
}
