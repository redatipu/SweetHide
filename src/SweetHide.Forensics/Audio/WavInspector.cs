using System.Globalization;

using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Lsb;

namespace SweetHide.Forensics.Audio;

/// <summary>Which samples contribute bits.</summary>
public enum AudioChannels
{
    /// <summary>Every sample, in file order (left, right, left, ...).</summary>
    All,

    Left,

    /// <summary>Second channel; only for stereo.</summary>
    Right,
}

/// <param name="BitPlane">0 = least significant bit of each sample.</param>
/// <param name="Channels">Which samples are read.</param>
/// <param name="BitOrder">Bit packing order.</param>
public sealed record WavLsbConfig(int BitPlane, AudioChannels Channels, BitOrder BitOrder)
{
    /// <summary>"bit0,all,msb".</summary>
    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"bit{BitPlane},{Channels.ToString().ToLowerInvariant()},{(BitOrder == BitOrder.MsbFirst ? "msb" : "lsb")}");

    public override string ToString() => Name;
}

public sealed record WavLsbScanOptions
{
    public IReadOnlyList<int> BitPlanes { get; init; } = [0, 1, 2, 3];

    public int SampleBytes { get; init; } = 4096;

    public double MinScore { get; init; } = 0.3;

    public int MaxResults { get; init; } = 16;

    public ContentAnalysisOptions Content { get; init; } = ContentAnalysisOptions.Default;

    public static WavLsbScanOptions Default { get; } = new();
}

/// <param name="Config">How the bits were read.</param>
/// <param name="Content">What the first bytes look like, and the score.</param>
/// <param name="StreamCapacity">Total bytes this configuration can carry.</param>
public sealed record WavLsbScanResult(WavLsbConfig Config, ContentAnalysis Content, long StreamCapacity);

/// <summary>LSB brute force over audio samples, scored exactly like <see cref="LsbScanner"/> scores images.</summary>
public static class WavInspector
{
    public static IReadOnlyList<WavLsbScanResult> ScanLsb(WavAudio audio, WavLsbScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        options ??= WavLsbScanOptions.Default;
        var sample = new byte[options.SampleBytes];
        var results = new List<WavLsbScanResult>();
        foreach (var plane in options.BitPlanes.Where(p => p >= 0 && p < audio.BitsPerSample))
        {
            foreach (var channels in Enum.GetValues<AudioChannels>())
            {
                if (channels == AudioChannels.Right && audio.Channels < 2)
                {
                    continue;
                }

                foreach (var bitOrder in Enum.GetValues<BitOrder>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var config = new WavLsbConfig(plane, channels, bitOrder);
                    var capacity = StreamCapacity(audio, config);
                    var written = Extract(audio, config, sample.AsSpan(0, (int)Math.Min(sample.Length, capacity)), cancellationToken);
                    var content = ContentAnalyzer.Analyze(sample.AsSpan(0, written), capacity, options.Content);
                    if (content.Score >= options.MinScore)
                    {
                        results.Add(new WavLsbScanResult(config, content, capacity));
                    }
                }
            }
        }

        return [.. results.OrderByDescending(r => r.Content.Score).Take(options.MaxResults)];
    }

    public static long StreamCapacity(WavAudio audio, WavLsbConfig config)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(config);
        var samples = config.Channels == AudioChannels.All ? audio.FrameCount * audio.Channels : (config.Channels == AudioChannels.Right && audio.Channels < 2 ? 0 : audio.FrameCount);
        return samples / 8;
    }

    public static byte[] ExtractStream(WavAudio audio, WavLsbConfig config, int maxBytes = LsbScanner.DefaultMaxStreamBytes, CancellationToken cancellationToken = default)
    {
        Validate(audio, config);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var buffer = new byte[Math.Min(maxBytes, StreamCapacity(audio, config))];
        var written = Extract(audio, config, buffer, cancellationToken);
        return written == buffer.Length ? buffer : buffer[..written];
    }

    /// <summary>Just the payload a scan result points at (length field skipped, delimiter or declared length honoured).</summary>
    public static byte[] ExtractPayload(WavAudio audio, WavLsbScanResult result, int maxBytes = LsbScanner.DefaultMaxStreamBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var content = result.Content;
        var end = content.PayloadLength is { } length ? Math.Min(content.PayloadOffset + length, result.StreamCapacity) : result.StreamCapacity;
        var stream = ExtractStream(audio, result.Config, (int)Math.Min(end, (long)content.PayloadOffset + maxBytes), cancellationToken);
        return PayloadTrimmer.Trim(stream, content, maxBytes);
    }

    private static int Extract(WavAudio audio, WavLsbConfig config, Span<byte> destination, CancellationToken cancellationToken)
    {
        var (first, step) = config.Channels switch
        {
            AudioChannels.Left => (0, audio.Channels),
            AudioChannels.Right => (1, audio.Channels),
            _ => (0, 1),
        };

        var total = audio.FrameCount * audio.Channels;
        var written = 0;
        var accumulator = 0;
        var bits = 0;
        var msbFirst = config.BitOrder == BitOrder.MsbFirst;
        for (long index = first; index < total && written < destination.Length; index += step)
        {
            if ((index & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var bit = (audio.RawSample(index) >> config.BitPlane) & 1;
            accumulator = msbFirst ? (accumulator << 1) | bit : accumulator | (bit << bits);
            if (++bits == 8)
            {
                destination[written++] = (byte)accumulator;
                accumulator = 0;
                bits = 0;
            }
        }

        return written;
    }

    private static void Validate(WavAudio audio, WavLsbConfig config)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(config.BitPlane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(config.BitPlane, audio.BitsPerSample);
    }
}
