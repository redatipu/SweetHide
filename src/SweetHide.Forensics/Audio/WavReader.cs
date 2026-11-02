using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

using SweetHide.Forensics.Containers;

namespace SweetHide.Forensics.Audio;

/// <summary>Uncompressed PCM audio read from a WAV file. Holds a slice of the file, not a copy.</summary>
public sealed class WavAudio
{
    internal WavAudio(int channels, int sampleRate, int bitsPerSample, long dataOffset, ReadOnlyMemory<byte> data)
    {
        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        DataOffset = dataOffset;
        Data = data;
    }

    /// <summary>1 (mono) or 2 (stereo).</summary>
    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>8 (unsigned) or 16 (signed little-endian).</summary>
    public int BitsPerSample { get; }

    /// <summary>File offset of the first sample.</summary>
    public long DataOffset { get; }

    /// <summary>The raw sample bytes, whole frames only.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    public int BytesPerSample => BitsPerSample / 8;

    public long FrameCount => Data.Length / (BytesPerSample * Channels);

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    /// <summary>The stored bits of one sample (8-bit: 0–255, 16-bit: the two bytes as an unsigned value).</summary>
    public int RawSample(long index)
    {
        var span = Data.Span;
        return BytesPerSample == 1 ? span[(int)index] : BinaryPrimitives.ReadUInt16LittleEndian(span[(int)(index * 2)..]);
    }

    /// <summary>The sample as a signed value in −1 … 1.</summary>
    public double NormalizedSample(long frame, int channel)
    {
        var index = (frame * Channels) + channel;
        return BytesPerSample == 1 ? (RawSample(index) - 128) / 128.0 : (short)RawSample(index) / 32768.0;
    }
}

/// <summary>Parses PCM WAV files (8/16-bit, mono/stereo). Every chunk length is checked against the file.</summary>
public static class WavReader
{
    private const int MaxSampleRate = 1_000_000;

    /// <exception cref="InvalidDataException">The file is not a WAV file this reader supports; the message says why.</exception>
    public static WavAudio Read(ReadOnlyMemory<byte> file) =>
        TryRead(file, out var audio, out var error) ? audio : throw new InvalidDataException(error);

    public static bool TryRead(ReadOnlyMemory<byte> file, [NotNullWhen(true)] out WavAudio? audio, [NotNullWhen(false)] out string? error)
    {
        audio = null;
        var data = file.Span;
        if (data.Length < 12 || !data.StartsWith("RIFF"u8) || !data[8..].StartsWith("WAVE"u8))
        {
            error = "Not a RIFF/WAVE file.";
            return false;
        }

        var walk = RiffWalker.Walk(data, new ContainerInspectorOptions { MaxStructureItems = 10_000 });
        var format = walk.Items.FirstOrDefault(i => i.Type == "fmt ");
        var samples = walk.Items.FirstOrDefault(i => i.Type == "data");
        if (format is null || format.DataLength < 16)
        {
            error = "No valid fmt chunk.";
            return false;
        }

        if (samples is null)
        {
            error = "No data chunk.";
            return false;
        }

        var fmt = data.Slice((int)format.DataOffset, (int)format.DataLength);
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == 0xFFFE && fmt.Length >= 26)
        {
            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // WAVE_FORMAT_EXTENSIBLE: sub-format GUID starts with the tag
        }

        if (tag != 1)
        {
            error = $"Only PCM audio is supported (format tag {tag}).";
            return false;
        }

        if (bits is not (8 or 16))
        {
            error = $"Only 8- and 16-bit samples are supported ({bits}-bit).";
            return false;
        }

        if (channels is not (1 or 2))
        {
            error = $"Only mono and stereo are supported ({channels} channels).";
            return false;
        }

        if (sampleRate is 0 or > MaxSampleRate)
        {
            error = $"Implausible sample rate {sampleRate}.";
            return false;
        }

        var frameBytes = channels * bits / 8;
        var length = (int)(samples.DataLength / frameBytes * frameBytes);
        audio = new WavAudio(channels, (int)sampleRate, bits, samples.DataOffset, file.Slice((int)samples.DataOffset, length));
        error = null;
        return true;
    }
}
