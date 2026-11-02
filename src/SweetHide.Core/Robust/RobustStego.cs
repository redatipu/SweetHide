using System.Buffers.Binary;

using SweetHide.Core.Imaging;
using SweetHide.Core.Jpeg;

namespace SweetHide.Core.Robust;

/// <summary>Outcome of <see cref="RobustStego.Extract(PixelBuffer, ReadOnlySpan{byte}, RobustProfile)"/>.</summary>
public enum RobustStatus
{
    /// <summary>No sync pattern for this key: the image carries nothing (or was made with another key or profile).</summary>
    NotFound = 0,

    /// <summary>The payload was recovered and its checksum matches.</summary>
    Found,

    /// <summary>The sync pattern is there, but too many bits were lost to recover the payload.</summary>
    Damaged,
}

/// <param name="Status">What happened.</param>
/// <param name="Payload">The recovered bytes when <paramref name="Status"/> is <see cref="RobustStatus.Found"/>, else empty.</param>
/// <param name="Profile">The profile whose sync pattern matched, if any.</param>
/// <param name="SyncScore">Correlation with the keyed sync pattern: about 0 for unmarked images, 1 for pristine ones.</param>
/// <param name="RawBitErrorRate">Share of sync slots that read back wrong: the channel's raw error rate before any correction.</param>
/// <param name="CorrectedSymbols">Bytes fixed by Reed–Solomon in the header and body.</param>
public sealed record RobustExtraction(
    RobustStatus Status,
    ReadOnlyMemory<byte> Payload,
    RobustProfile? Profile,
    double SyncScore,
    double RawBitErrorRate,
    int CorrectedSymbols);

/// <summary>
/// "Robust mode": hides a short payload (up to a few hundred bytes) in a photo so that it survives being sent through
/// a chat app that resizes and re-encodes images as JPEG. Pure signal processing: no encryption, no authentication.
/// Callers should seal the payload (e.g. AES-GCM) before embedding and derive the 32-byte key from the password.
/// </summary>
/// <remarks>
/// <para>
/// The cover is first shrunk to the profile's safe size, so the app has no reason to resize it again and its 8x8
/// JPEG grid lines up with ours. Each 8x8 block of luminance carries bits in a few key-chosen low-mid frequency DCT
/// coefficients, by dithered quantization index modulation (QIM): the coefficient is moved to the nearest point of
/// one of two interleaved lattices with spacing <see cref="RobustProfile.Step"/>. A JPEG re-encode moves a coefficient
/// by at most half its quantizer step, so as long as that stays under a quarter of our step the bit reads back
/// unchanged.
/// </para>
/// <para>
/// Layout (see <see cref="RobustLayout"/>): a 64-bit keyed sync pattern, a Reed–Solomon protected header (format
/// version and payload length), and the body (payload ‖ CRC-32, split into Reed–Solomon codewords), each bit repeated
/// and spread over the image by a keyed permutation. Extraction sums the soft decisions of every copy of a bit, marks
/// shaky bytes as erasures, and decodes. A low sync correlation means "nothing here" rather than "damaged".
/// </para>
/// </remarks>
public static class RobustStego
{
    public const int KeySize = 32;

    /// <summary>Hard cap on payload size, whatever the image size.</summary>
    public const int MaxPayloadLength = 4096;

    private const byte FormatVersion = 1;
    private const byte HeaderCheck = 0xA5;
    private const double SyncThreshold = 0.2;
    private const int MaxEmbedIterations = 6;

    /// <summary>Size the cover is shrunk to for <paramref name="profile"/> (never enlarged).</summary>
    public static (int Width, int Height) TargetSize(int width, int height, RobustProfile profile)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        var scale = Math.Min(1.0, Math.Min((double)profile.MaxLongSide / Math.Max(width, height), Math.Sqrt((double)profile.MaxPixels / ((double)width * height))));
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        while ((long)w * h > profile.MaxPixels || Math.Max(w, h) > profile.MaxLongSide)
        {
            w = Math.Max(1, w - (w >= h ? 1 : 0));
            h = Math.Max(1, h - (h > w ? 1 : 0));
        }

        return (w, h);
    }

    /// <summary>Largest payload a cover of this size can carry with <paramref name="profile"/> (after shrinking).</summary>
    public static int MaxPayloadBytes(int width, int height, RobustProfile profile)
    {
        var (w, h) = TargetSize(width, height, profile);
        return new RobustLayout(w, h, new byte[KeySize], profile).MaxPayloadBytes();
    }

    /// <summary>
    /// The cover as <see cref="Embed"/> will use it: shrunk to <see cref="TargetSize"/> (antialiased bicubic) and
    /// composited over white. Useful for previews and for measuring the distortion embedding adds.
    /// </summary>
    public static PixelBuffer PrepareCover(PixelBuffer cover, RobustProfile profile)
    {
        ArgumentNullException.ThrowIfNull(cover);
        var (width, height) = TargetSize(cover.Width, cover.Height, profile);
        return ImageResampler.Resize(cover, width, height);
    }

    /// <summary>
    /// Returns a new, opaque image at <see cref="TargetSize"/> carrying <paramref name="payload"/>. Encode it as
    /// JPEG (<see cref="WriteJpeg"/>) or PNG before sending. The cover is not modified.
    /// </summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public static PixelBuffer Embed(PixelBuffer cover, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, RobustProfile profile)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(profile);
        RequireKey(key);
        var (width, height) = TargetSize(cover.Width, cover.Height, profile);
        var layout = new RobustLayout(width, height, key, profile);
        if (!layout.Fits(payload.Length))
        {
            var capacity = layout.Fits(0) ? $"{layout.MaxPayloadBytes():N0} bytes" : "nothing";
            throw new StegoException(
                StegoError.InsufficientCapacity,
                $"Needs {payload.Length:N0} bytes but a {width}x{height} image holds {capacity} in {profile.Name} robust mode.");
        }

        var bits = SlotBits(layout, payload);
        var image = ImageResampler.Resize(cover, width, height);
        var pixels = image.Data;
        var step = profile.Step;
        var perBlock = profile.CoefficientsPerBlock;
        Parallel.For(0, layout.BlocksHigh, by =>
        {
            Span<float> targets = stackalloc float[perBlock];
            Span<float> luma = stackalloc float[64];
            Span<float> delta = stackalloc float[64];
            for (var bx = 0; bx < layout.BlocksWide; bx++)
            {
                var block = (by * layout.BlocksWide) + bx;
                var firstSlot = block * perBlock;
                ReadLuma(pixels, width, bx, by, luma);
                for (var j = 0; j < perBlock; j++)
                {
                    var slot = firstSlot + j;
                    var c = BlockDct.Coefficient(luma, layout.Coefficient[slot]);
                    targets[j] = Quantize(c, layout.Dither[slot], step, bits[slot]);
                }

                // Push the luminance (equally on R, G, B, which leaves chroma alone) until the coefficients sit on
                // their targets despite rounding to bytes and clipping at 0/255.
                for (var iteration = 0; iteration < MaxEmbedIterations; iteration++)
                {
                    delta.Clear();
                    var worst = 0f;
                    for (var j = 0; j < perBlock; j++)
                    {
                        var k = layout.Coefficient[firstSlot + j];
                        var error = targets[j] - BlockDct.Coefficient(luma, k);
                        worst = Math.Max(worst, Math.Abs(error));
                        var basis = BlockDct.Basis.Slice(k * 64, 64);
                        for (var p = 0; p < 64; p++)
                        {
                            delta[p] += error * basis[p];
                        }
                    }

                    if (worst < 0.75f)
                    {
                        break;
                    }

                    AddLuma(pixels, width, bx, by, delta);
                    ReadLuma(pixels, width, bx, by, luma);
                }
            }
        });

        return image;
    }

    /// <summary>Looks for a payload embedded with <paramref name="profile"/> and <paramref name="key"/>.</summary>
    /// <remarks>
    /// If nothing is found at the image's own size and the image looks like a slightly shrunk copy of something this
    /// profile produced (0.6–1x), it is resampled back to the profile's size and tried again. This is best effort.
    /// </remarks>
    public static RobustExtraction Extract(PixelBuffer image, ReadOnlySpan<byte> key, RobustProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Search(image, key, [profile]);
    }

    /// <summary>Tries every built-in profile, first at the image's own size, then rescaled.</summary>
    public static RobustExtraction Extract(PixelBuffer image, ReadOnlySpan<byte> key) => Search(image, key, RobustProfile.All);

    /// <summary>Writes a baseline 4:2:0 JPEG at the profile's output quality using the built-in encoder.</summary>
    public static void WriteJpeg(PixelBuffer image, Stream output, RobustProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        JpegEncoder.Encode(image, output, profile.OutputJpegQuality, JpegSubsampling.Yuv420, new JpegWriteOptions { OptimizeHuffmanTables = true });
    }

    private static RobustExtraction Search(PixelBuffer image, ReadOnlySpan<byte> key, IReadOnlyList<RobustProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequireKey(key);
        foreach (var profile in profiles)
        {
            profile.Validate();
        }

        // A "damaged" result means the sync was found at this size, so rescaling would not help.
        RobustExtraction? damaged = null;
        RobustExtraction? first = null;
        foreach (var profile in profiles)
        {
            var result = ExtractAtSize(image, key, profile);
            if (result.Status == RobustStatus.Found)
            {
                return result;
            }

            first ??= result;
            damaged ??= result.Status == RobustStatus.Damaged ? result : null;
        }

        if (damaged is not null)
        {
            return damaged;
        }

        foreach (var profile in profiles)
        {
            foreach (var (w, h) in RescaleCandidates(image.Width, image.Height, profile))
            {
                var result = ExtractAtSize(ImageResampler.Resize(image, w, h), key, profile);
                if (result.Status != RobustStatus.NotFound)
                {
                    return result;
                }
            }
        }

        return first!;
    }

    private static RobustExtraction ExtractAtSize(PixelBuffer image, ReadOnlySpan<byte> key, RobustProfile profile)
    {
        var layout = new RobustLayout(image.Width, image.Height, key, profile);
        if (!layout.Fits(0))
        {
            return new RobustExtraction(RobustStatus.NotFound, ReadOnlyMemory<byte>.Empty, null, 0, 0.5, 0);
        }

        var soft = SoftDecisions(image, layout);

        // Sync: mean agreement with the keyed pattern, and the raw error rate it implies.
        double agreement = 0;
        var wrong = 0;
        for (var i = 0; i < layout.SyncSlotCount; i++)
        {
            var sign = layout.SyncPattern[i % RobustLayout.SyncBits] == 0 ? 1 : -1;
            var s = soft[layout.Physical(i)] * sign;
            agreement += s;
            wrong += s < 0 ? 1 : 0;
        }

        var score = agreement / layout.SyncSlotCount;
        var rawErrors = (double)wrong / layout.SyncSlotCount;
        if (score < SyncThreshold)
        {
            return new RobustExtraction(RobustStatus.NotFound, ReadOnlyMemory<byte>.Empty, null, score, rawErrors, 0);
        }

        RobustExtraction Damaged(int corrected) => new(RobustStatus.Damaged, ReadOnlyMemory<byte>.Empty, profile, score, rawErrors, corrected);

        var header = Combine(soft, layout, layout.SyncSlotCount, layout.HeaderSlotCount - layout.SyncSlotCount, RobustLayout.HeaderBits, out var headerReliability);
        if (!DecodeWithErasures(new ReedSolomon(RobustLayout.HeaderParityBytes), header, headerReliability, out var corrected))
        {
            return Damaged(0);
        }

        var length = header[1] | (header[2] << 8);
        if (header[0] != FormatVersion || header[3] != HeaderCheck || length > layout.MaxPayloadBytes())
        {
            return Damaged(corrected);
        }

        var codewords = RobustLayout.Codewords(length, profile.ParityPercent);
        var bodyBits = codewords.Sum(c => c.Data + c.Parity) * 8;
        var body = Combine(soft, layout, layout.HeaderSlotCount, layout.BodySlotCount, bodyBits, out var bodyReliability);
        var data = new byte[length + RobustLayout.CrcBytes];
        int offset = 0, written = 0;
        foreach (var (dataLength, parityLength) in codewords)
        {
            var word = body.AsSpan(offset, dataLength + parityLength).ToArray();
            if (!DecodeWithErasures(new ReedSolomon(parityLength), word, bodyReliability.AsSpan(offset, word.Length), out var fixedSymbols))
            {
                return Damaged(corrected);
            }

            corrected += fixedSymbols;
            word.AsSpan(0, dataLength).CopyTo(data.AsSpan(written));
            written += dataLength;
            offset += dataLength + parityLength;
        }

        var payload = data.AsMemory(0, length);
        if (Crc32.Compute(payload.Span) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(length)))
        {
            return Damaged(corrected);
        }

        return new RobustExtraction(RobustStatus.Found, payload, profile, score, rawErrors, corrected);
    }

    /// <summary>The bit every physical slot must carry.</summary>
    private static byte[] SlotBits(RobustLayout layout, ReadOnlySpan<byte> payload)
    {
        var profile = layout.Profile;
        var header = new byte[RobustLayout.HeaderDataBytes + RobustLayout.HeaderParityBytes];
        header[0] = FormatVersion;
        header[1] = (byte)payload.Length;
        header[2] = (byte)(payload.Length >> 8);
        header[3] = HeaderCheck;
        new ReedSolomon(RobustLayout.HeaderParityBytes).Encode(header.AsSpan(0, RobustLayout.HeaderDataBytes), header.AsSpan(RobustLayout.HeaderDataBytes));

        var data = new byte[payload.Length + RobustLayout.CrcBytes];
        payload.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(payload.Length), Crc32.Compute(payload));
        var codewords = RobustLayout.Codewords(payload.Length, profile.ParityPercent);
        var body = new byte[codewords.Sum(c => c.Data + c.Parity)];
        int read = 0, offset = 0;
        foreach (var (dataLength, parityLength) in codewords)
        {
            data.AsSpan(read, dataLength).CopyTo(body.AsSpan(offset));
            new ReedSolomon(parityLength).Encode(body.AsSpan(offset, dataLength), body.AsSpan(offset + dataLength, parityLength));
            read += dataLength;
            offset += dataLength + parityLength;
        }

        var bits = new byte[layout.SlotCount];
        for (var i = 0; i < layout.SlotCount; i++)
        {
            bits[layout.Physical(i)] = i < layout.SyncSlotCount
                ? layout.SyncPattern[i % RobustLayout.SyncBits]
                : i < layout.HeaderSlotCount
                    ? Bit(header, (i - layout.SyncSlotCount) % RobustLayout.HeaderBits)
                    : Bit(body, (i - layout.HeaderSlotCount) % (body.Length * 8));
        }

        return bits;
    }

    /// <summary>Soft decision per physical slot: +1 squarely on the "0" lattice, -1 squarely on the "1" lattice.</summary>
    private static float[] SoftDecisions(PixelBuffer image, RobustLayout layout)
    {
        var soft = new float[layout.SlotCount];
        var pixels = image.Data;
        var perBlock = layout.Profile.CoefficientsPerBlock;
        var step = layout.Profile.Step;
        Parallel.For(0, layout.BlocksHigh, by =>
        {
            Span<float> luma = stackalloc float[64];
            for (var bx = 0; bx < layout.BlocksWide; bx++)
            {
                ReadLuma(pixels, image.Width, bx, by, luma);
                var firstSlot = ((by * layout.BlocksWide) + bx) * perBlock;
                for (var j = 0; j < perBlock; j++)
                {
                    var slot = firstSlot + j;
                    var t = (BlockDct.Coefficient(luma, layout.Coefficient[slot]) - layout.Dither[slot]) / step;
                    var fraction = t - MathF.Floor(t);
                    var distance = Math.Min(fraction, 1 - fraction); // to the nearest "0" lattice point, in steps
                    soft[slot] = 1 - (4 * distance);
                }
            }
        });

        return soft;
    }

    /// <summary>Sums the soft decisions of every copy of each bit; returns bytes and a reliability per byte.</summary>
    private static byte[] Combine(float[] soft, RobustLayout layout, int firstLogical, int slotCount, int bitCount, out float[] reliability)
    {
        var sums = new float[bitCount];
        var counts = new int[bitCount];
        for (var i = 0; i < slotCount; i++)
        {
            var bit = i % bitCount;
            sums[bit] += soft[layout.Physical(firstLogical + i)];
            counts[bit]++;
        }

        var bytes = new byte[bitCount / 8];
        reliability = new float[bytes.Length];
        Array.Fill(reliability, float.MaxValue);
        for (var bit = 0; bit < bitCount; bit++)
        {
            if (sums[bit] < 0)
            {
                bytes[bit / 8] |= (byte)(0x80 >> (bit % 8));
            }

            var confidence = counts[bit] == 0 ? 0 : Math.Abs(sums[bit]) / counts[bit];
            reliability[bit / 8] = Math.Min(reliability[bit / 8], confidence);
        }

        return bytes;
    }

    /// <summary>
    /// Tries plain error correction first, then treats growing numbers of the least reliable bytes as erasures
    /// (always keeping some redundancy spare, so a "correction" is still checked).
    /// </summary>
    private static bool DecodeWithErasures(ReedSolomon codec, byte[] word, ReadOnlySpan<float> reliability, out int corrected)
    {
        var order = Enumerable.Range(0, word.Length).ToArray();
        var keys = reliability.ToArray();
        Array.Sort(keys, order);
        var parity = codec.ParitySymbols;
        var tried = new HashSet<int>();
        foreach (var fraction in new[] { 0, 1, 2, 3 })
        {
            var erasures = Math.Max(0, Math.Min(parity * fraction / 4, parity - 2));
            if (!tried.Add(erasures))
            {
                continue;
            }

            var attempt = (byte[])word.Clone();
            if (codec.TryDecode(attempt, order.AsSpan(0, erasures), out corrected))
            {
                attempt.CopyTo(word, 0);
                return true;
            }
        }

        corrected = 0;
        return false;
    }

    /// <summary>Sizes to try when the image may have been rescaled after embedding.</summary>
    private static IEnumerable<(int Width, int Height)> RescaleCandidates(int width, int height, RobustProfile profile)
    {
        // Our output for a large cover with this aspect ratio, i.e. the size the app would have scaled from.
        var large = 1 << 16;
        var (w, h) = width >= height
            ? TargetSize(large, Math.Max(1, (int)Math.Round((double)large * height / width)), profile)
            : TargetSize(Math.Max(1, (int)Math.Round((double)large * width / height)), large, profile);
        // Only for images that look shrunk (down to 0.6x); anything else, such as a full-size camera photo, is not
        // something this profile produced, and resampling it would just waste time.
        var ratio = (double)Math.Max(width, height) / Math.Max(w, h);
        if ((w == width && h == height) || ratio is < 0.6 or >= 1.0)
        {
            yield break;
        }

        // The aspect ratio of a rescaled copy is only accurate to a pixel, so try the neighbours too.
        (int, int)[] candidates = [(w, h), (w, h - 1), (w, h + 1), (w - 1, h), (w + 1, h)];
        foreach (var candidate in candidates)
        {
            if (candidate.Item1 >= 8 && candidate.Item2 >= 8 && (long)candidate.Item1 * candidate.Item2 <= profile.MaxPixels)
            {
                yield return candidate;
            }
        }
    }

    private static float Quantize(float value, float dither, float step, int bit)
    {
        var offset = dither + (bit * step / 2);
        return offset + (MathF.Round((value - offset) / step) * step);
    }

    private static byte Bit(byte[] data, int index) => (byte)((data[index / 8] >> (7 - (index % 8))) & 1);

    private static void ReadLuma(byte[] pixels, int width, int bx, int by, Span<float> luma)
    {
        for (var y = 0; y < 8; y++)
        {
            var row = ((((by * 8) + y) * width) + (bx * 8)) * 4;
            for (var x = 0; x < 8; x++)
            {
                var p = row + (x * 4);
                luma[(y * 8) + x] = (0.299f * pixels[p]) + (0.587f * pixels[p + 1]) + (0.114f * pixels[p + 2]);
            }
        }
    }

    private static void AddLuma(byte[] pixels, int width, int bx, int by, ReadOnlySpan<float> delta)
    {
        for (var y = 0; y < 8; y++)
        {
            var row = ((((by * 8) + y) * width) + (bx * 8)) * 4;
            for (var x = 0; x < 8; x++)
            {
                var p = row + (x * 4);
                var d = delta[(y * 8) + x];
                for (var c = 0; c < 3; c++)
                {
                    pixels[p + c] = (byte)Math.Clamp((int)MathF.Round(pixels[p + c] + d), 0, 255);
                }
            }
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The key must be {KeySize} bytes.", nameof(key));
        }
    }
}
