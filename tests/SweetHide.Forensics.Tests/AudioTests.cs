using FsCheck;
using FsCheck.Fluent;

using SweetHide.Forensics.Audio;
using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Lsb;

using static SweetHide.Forensics.Tests.FileBuilders;
using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class WavTests
{
    /// <summary>Overwrites bit 0 of the selected samples with the payload, MSB first.</summary>
    private static short[] HideInSamples(short[] samples, byte[] payload, int first, int step)
    {
        var copy = (short[])samples.Clone();
        var index = first;
        foreach (var b in payload)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                copy[index] = (short)((copy[index] & ~1) | ((b >> bit) & 1));
                index += step;
            }
        }

        return copy;
    }

    [Fact]
    public void Parses_pcm_wav()
    {
        var wav = Wav(2, 8000, 16, Pcm16([0, 0, 16384, -16384, 32767, -32768]));

        var audio = WavReader.Read(wav);

        Assert.Equal((2, 8000, 16, 3L), (audio.Channels, audio.SampleRate, audio.BitsPerSample, audio.FrameCount));
        Assert.Equal(44, audio.DataOffset);
        Assert.Equal(TimeSpan.FromSeconds(3.0 / 8000), audio.Duration);
        Assert.Equal(0.5, audio.NormalizedSample(1, 0));
        Assert.Equal(-0.5, audio.NormalizedSample(1, 1));
        Assert.Equal(-1.0, audio.NormalizedSample(2, 1));

        var eightBit = WavReader.Read(Wav(1, 8000, 8, [128, 255, 0]));
        Assert.Equal(0, eightBit.NormalizedSample(0, 0));
        Assert.Equal(-1, eightBit.NormalizedSample(2, 0));
    }

    [Fact]
    public void Rejects_what_it_does_not_support_with_a_reason()
    {
        AssertRejected(Wav(1, 8000, 16, new byte[8], formatTag: 3), "PCM");
        AssertRejected(Wav(1, 8000, 24, new byte[9]), "16-bit");
        AssertRejected(Wav(6, 8000, 16, new byte[24]), "stereo");
        AssertRejected(Wav(1, 0, 16, new byte[8]), "sample rate");
        AssertRejected(Riff("WAVE", ("data", new byte[8])), "fmt");
        AssertRejected(Riff("WAVE", ("fmt ", new byte[16])), "data");
        AssertRejected(Png(), "RIFF");
        AssertRejected([], "RIFF");
    }

    [Fact]
    public void Reads_what_is_there_when_the_data_chunk_lies_about_its_size()
    {
        var wav = Wav(2, 8000, 16, new byte[100]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 1_000_000);

        var audio = WavReader.Read(wav);

        Assert.Equal(25, audio.FrameCount); // 100 bytes of 4-byte frames
    }

    [Fact]
    public void Finds_text_hidden_in_every_sample()
    {
        var samples = HideInSamples(Tone(8000, 2, 8000, 440), Utf8("flag{listen_to_the_bits}\0"), 0, 1);
        var audio = WavReader.Read(Wav(2, 8000, 16, Pcm16(samples)));

        var results = WavInspector.ScanLsb(audio, cancellationToken: Ct);

        var top = results[0];
        Assert.Equal("bit0,all,msb", top.Config.Name);
        Assert.Equal(ContentKind.TerminatedText, top.Content.Kind);
        Assert.Equal("flag{listen_to_the_bits}"u8.ToArray(), WavInspector.ExtractPayload(audio, top, cancellationToken: Ct));
    }

    [Fact]
    public void Finds_text_hidden_in_one_channel_of_8_bit_audio()
    {
        var tone = Tone(4000, 2, 8000, 300).Select(s => (short)((s >> 8) + 128)).ToArray();
        var hidden = HideInSamples(tone, Utf8("left channel secret, ลับมาก\0"), 0, 2);
        var audio = WavReader.Read(Wav(2, 8000, 8, [.. hidden.Select(s => (byte)s)]));

        var top = WavInspector.ScanLsb(audio, cancellationToken: Ct)[0];

        Assert.Equal(new WavLsbConfig(0, AudioChannels.Left, BitOrder.MsbFirst), top.Config);
        Assert.Equal("left channel secret, ลับมาก", top.Content.Preview);
    }

    [Fact]
    public void Clean_audio_produces_no_confident_results()
    {
        var audio = WavReader.Read(Wav(2, 8000, 16, Pcm16(Tone(8000, 2, 8000, 440))));

        Assert.All(WavInspector.ScanLsb(audio, cancellationToken: Ct), r => Assert.True(r.Content.Score < 0.5, $"{r.Config}: {r.Content.Preview}"));
    }

    [Fact]
    public void Extract_stream_respects_capacity_and_limits()
    {
        var audio = WavReader.Read(Wav(1, 8000, 16, Pcm16(new short[100])));
        var config = new WavLsbConfig(0, AudioChannels.All, BitOrder.MsbFirst);

        Assert.Equal(12, WavInspector.StreamCapacity(audio, config));
        Assert.Equal(12, WavInspector.ExtractStream(audio, config, cancellationToken: Ct).Length);
        Assert.Equal(0, WavInspector.StreamCapacity(audio, config with { Channels = AudioChannels.Right }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavInspector.ExtractStream(audio, config with { BitPlane = 16 }, cancellationToken: Ct));
    }

    [Fact]
    public void Fft_finds_a_pure_tone()
    {
        var fft = new Fft(64);
        var real = Enumerable.Range(0, 64).Select(i => Math.Cos(2 * Math.PI * 5 * i / 64)).ToArray();
        var imaginary = new double[64];

        fft.Transform(real, imaginary);

        var magnitudes = real.Zip(imaginary, (r, i) => Math.Sqrt((r * r) + (i * i))).ToArray();
        Assert.Equal(32, magnitudes[5], 6);
        Assert.Equal(32, magnitudes[59], 6);
        Assert.All(magnitudes.Where((_, k) => k is not 5 and not 59), m => Assert.True(m < 1e-9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(48));
    }

    [Fact]
    public void Spectrogram_shows_a_tone_where_and_when_it_plays()
    {
        // 1 s at 8 kHz: silence (with faint noise), and a 2 kHz tone from 0.25 s to 0.5 s.
        const int rate = 8000;
        var random = new Random(3);
        var samples = new short[rate];
        for (var i = 0; i < rate; i++)
        {
            var tone = i is >= rate / 4 and < rate / 2 ? 12000 * Math.Sin(2 * Math.PI * 2000 * i / rate) : 0;
            samples[i] = (short)(tone + random.Next(-20, 21));
        }

        var audio = WavReader.Read(Wav(1, rate, 16, Pcm16(samples)));
        var image = Spectrogram.Render(audio, new SpectrogramOptions { FftSize = 256, Palette = SpectrogramPalette.Grayscale }, Ct);

        Assert.Equal(128, image.Height);
        var columnsPerSecond = image.Width / (double)audio.Duration.TotalSeconds;
        var row = 127 - 64; // 2 kHz / 8 kHz * 256 = bin 64, low frequencies at the bottom
        byte At(double seconds, int y) => image.Pixels[((y * image.Width) + (int)(seconds * columnsPerSecond)) * 4];

        Assert.True(At(0.375, row) > 200, $"tone: {At(0.375, row)}");
        Assert.True(At(0.75, row) < 120, $"after: {At(0.75, row)}");
        Assert.True(At(0.375, 20) < 120, $"other frequency: {At(0.375, 20)}");
    }

    [Fact]
    public void Spectrogram_width_is_capped()
    {
        var audio = WavReader.Read(Wav(1, 8000, 16, Pcm16(Tone(80_000, 1, 8000, 1000))));

        var image = Spectrogram.Render(audio, new SpectrogramOptions { FftSize = 128, MaxWidth = 300 }, Ct);

        Assert.True(image.Width <= 300);
        Assert.Equal(64, image.Height);
    }

    [Fact]
    public void Random_mutations_only_ever_fail_with_invalid_data()
    {
        var seed = Wav(2, 8000, 16, Pcm16(Tone(200, 2, 8000, 440)));
        var gen =
            from flips in Gen.ListOf(Gen.Zip(Gen.Choose(0, 64), Gen.Choose(0, 255)))
            from cut in Gen.Choose(0, seed.Length)
            select HostileInputTests.Mutate(seed, flips, cut);

        Prop.ForAll(gen.ToArbitrary(), file =>
        {
            try
            {
                var audio = WavReader.Read(file);
                WavInspector.ScanLsb(audio, cancellationToken: Ct);
                Spectrogram.Render(audio, new SpectrogramOptions { FftSize = 64 }, Ct);
                return audio.Data.Length <= file.Length;
            }
            catch (InvalidDataException)
            {
                return !WavReader.TryRead(file, out _, out _);
            }
        }).QuickCheckThrowOnFailure();
    }

    private static void AssertRejected(byte[] file, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => WavReader.Read(file));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.False(WavReader.TryRead(file, out var audio, out _));
        Assert.Null(audio);
    }
}
