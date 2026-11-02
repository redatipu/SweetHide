using SweetHide.Core.Imaging;
using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Lsb;

public sealed record LsbScanOptions
{
    /// <summary>Bytes extracted and scored per configuration. Only this many pixels are read for each one.</summary>
    public int SampleBytes { get; init; } = 4096;

    public IReadOnlyList<int> BitPlanes { get; init; } = [0, 1, 2, 3, 4, 5, 6, 7];

    public IReadOnlyList<LsbChannels> Channels { get; init; } = Enum.GetValues<LsbChannels>();

    public IReadOnlyList<PixelOrder> PixelOrders { get; init; } = Enum.GetValues<PixelOrder>();

    public IReadOnlyList<BitOrder> BitOrders { get; init; } = Enum.GetValues<BitOrder>();

    /// <summary>Skip configurations that read alpha when every pixel is opaque (they only yield 0xFF bytes).</summary>
    public bool SkipAlphaWhenOpaque { get; init; } = true;

    /// <summary>Results scoring below this are dropped. Use 0 to list every configuration, like zsteg -a.</summary>
    public double MinScore { get; init; } = 0.3;

    public int MaxResults { get; init; } = 32;

    public ContentAnalysisOptions Content { get; init; } = ContentAnalysisOptions.Default;

    public static LsbScanOptions Default { get; } = new();
}

/// <param name="Config">How the bits were read.</param>
/// <param name="Content">What the first <see cref="LsbScanOptions.SampleBytes"/> bytes look like, and the score.</param>
/// <param name="StreamCapacity">Total bytes this configuration can carry in the image.</param>
public sealed record LsbScanResult(LsbConfig Config, ContentAnalysis Content, long StreamCapacity);

/// <summary>
/// zsteg-style brute force: reads the start of every bit plane × channel set × pixel order × bit order and ranks what
/// comes out (text, file magic, length-prefixed or delimited payloads).
/// </summary>
public static class LsbScanner
{
    /// <summary>Default cap for <see cref="ExtractStream"/>.</summary>
    public const int DefaultMaxStreamBytes = 64 * 1024 * 1024;

    /// <summary>Every configuration the options allow, in a stable order.</summary>
    public static IEnumerable<LsbConfig> Configurations(LsbScanOptions? options = null)
    {
        options ??= LsbScanOptions.Default;
        foreach (var plane in options.BitPlanes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(plane);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(plane, 7);
            foreach (var channels in options.Channels)
            {
                foreach (var order in options.PixelOrders)
                {
                    foreach (var bitOrder in options.BitOrders)
                    {
                        yield return new LsbConfig(plane, channels, order, bitOrder);
                    }
                }
            }
        }
    }

    /// <summary>Scores every configuration and returns the best first.</summary>
    public static IReadOnlyList<LsbScanResult> Scan(PixelBuffer image, LsbScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= LsbScanOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleBytes);

        var skipAlpha = options.SkipAlphaWhenOpaque && image.IsOpaque;
        var sample = new byte[options.SampleBytes];
        var results = new List<LsbScanResult>();
        foreach (var config in Configurations(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipAlpha && LsbConfig.UsesAlpha(config.Channels))
            {
                continue;
            }

            var capacity = StreamCapacity(image, config);
            var written = BitExtractor.Extract(image, config, sample.AsSpan(0, (int)Math.Min(sample.Length, capacity)), cancellationToken);
            var content = ContentAnalyzer.Analyze(sample.AsSpan(0, written), capacity, options.Content);
            if (content.Score >= options.MinScore)
            {
                results.Add(new LsbScanResult(config, content, capacity));
            }
        }

        // OrderBy is stable, so equal scores keep the simplest configuration (lowest plane) first.
        return [.. results.OrderByDescending(r => r.Content.Score).Take(options.MaxResults)];
    }

    /// <summary>Bytes a configuration can carry in this image (8 bits per byte, partial bytes dropped).</summary>
    public static long StreamCapacity(PixelBuffer image, LsbConfig config)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(config);
        return image.PixelCount * config.ChannelCount / 8;
    }

    /// <summary>Extracts the bit stream for one configuration from the start, up to <paramref name="maxBytes"/>.</summary>
    public static byte[] ExtractStream(PixelBuffer image, LsbConfig config, int maxBytes = DefaultMaxStreamBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        Validate(config);

        var buffer = new byte[Math.Min(maxBytes, StreamCapacity(image, config))];
        var written = BitExtractor.Extract(image, config, buffer, cancellationToken);
        return written == buffer.Length ? buffer : buffer[..written];
    }

    /// <summary>
    /// Extracts just the payload a scan result points at: skips a length field, stops at the declared length or the
    /// delimiter, and trims text that ran past the scanned sample at its first non-text byte.
    /// </summary>
    public static byte[] ExtractPayload(PixelBuffer image, LsbScanResult result, int maxBytes = DefaultMaxStreamBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var content = result.Content;
        var end = content.PayloadLength is { } length
            ? Math.Min(content.PayloadOffset + length, result.StreamCapacity)
            : result.StreamCapacity;
        var stream = ExtractStream(image, result.Config, (int)Math.Min(end, (long)content.PayloadOffset + maxBytes), cancellationToken);
        return PayloadTrimmer.Trim(stream, content, maxBytes);
    }

    private static void Validate(LsbConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(config.BitPlane);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(config.BitPlane, 7);
    }
}
