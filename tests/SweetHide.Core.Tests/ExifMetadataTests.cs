using System.Buffers.Binary;
using System.Text;

using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class ExifMetadataTests
{
    private const ushort Make = 0x010F;
    private const ushort Model = 0x0110;
    private const ushort Orientation = 0x0112;
    private const ushort XResolution = 0x011A;
    private const ushort ExifIfd = 0x8769;
    private const ushort GpsIfd = 0x8825;
    private const ushort ExposureTime = 0x829A;
    private const ushort DateTimeOriginal = 0x9003;
    private const ushort MakerNote = 0x927C;
    private const ushort PixelX = 0xA002;
    private const ushort PixelY = 0xA003;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Keeps_camera_details_and_drops_gps_maker_note_and_thumbnail(bool bigEndian)
    {
        var sanitized = ExifMetadata.Sanitize(CameraExif(bigEndian), 640, 480);

        var (ifd0, exif) = Parse(sanitized!);
        Assert.Equal("Canon", Ascii(ifd0[Make]));
        Assert.Equal("EOS R6 Mark II", Ascii(ifd0[Model]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ifd0[Orientation]));
        Assert.Equal((72u, 1u), Rational(ifd0[XResolution]));
        Assert.DoesNotContain(GpsIfd, ifd0.Keys);

        Assert.Equal("2026:11:02 10:00:00", Ascii(exif[DateTimeOriginal]));
        Assert.Equal((1u, 250u), Rational(exif[ExposureTime]));
        Assert.Equal(640u, BinaryPrimitives.ReadUInt32LittleEndian(exif[PixelX]));
        Assert.Equal(480u, BinaryPrimitives.ReadUInt32LittleEndian(exif[PixelY]));
        Assert.DoesNotContain(MakerNote, exif.Keys);

        // Nothing of the GPS block, maker note or thumbnail survives anywhere in the bytes.
        Assert.Equal(-1, sanitized.AsSpan().IndexOf("SECRET-SERIAL"u8));
        Assert.Equal(-1, sanitized.AsSpan().IndexOf("THUMBNAIL"u8));
        Assert.Equal(-1, sanitized.AsSpan().IndexOf(GpsDegreesLittleEndian)); // 13/1 degrees, as the sanitizer would write it
    }

    [Fact]
    public void Finds_exif_in_jpeg_png_and_webp_files()
    {
        var exif = CameraExif(bigEndian: true);

        Assert.Equal(exif, ExifMetadata.Extract(JpegWithExif(exif)));
        Assert.Equal(exif, ExifMetadata.Extract(ExifMetadata.AddToPng(Png(TestImages.Photo(8, 8)), exif)));
        Assert.Equal(exif, ExifMetadata.Extract(WebPWithExif(exif)));
        Assert.Null(ExifMetadata.Extract(Png(TestImages.Photo(8, 8))));
        Assert.Null(ExifMetadata.ForOutput(Png(TestImages.Photo(8, 8)), 8, 8));
    }

    [Fact]
    public void Stego_png_with_exif_still_reveals_and_other_readers_accept_it()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(64, 48);
        var stego = engine.Embed(cover, Payload.FromText("with exif"), "pw");
        var exif = ExifMetadata.ForOutput(JpegWithExif(CameraExif(bigEndian: true)), stego.Width, stego.Height)!;

        var dir = Directory.CreateTempSubdirectory("sweethide-exif-");
        try
        {
            var path = Path.Combine(dir.FullName, "out.png");
            new SkiaImageCodec().SaveImage(stego, path, overwrite: false, exif);
            var file = File.ReadAllBytes(path);

            Assert.Equal(exif, ExifMetadata.Extract(file));
            var decoded = new SkiaImageCodec().Decode(new MemoryStream(file), false);
            Assert.Equal(stego.Data, decoded.Data);
            Assert.Equal("with exif", engine.Extract(decoded, "pw").Entries[0].GetText());

            Assert.Throws<ArgumentException>(() => new SkiaImageCodec().SaveImage(stego, Path.Combine(dir.FullName, "out.webp"), false, exif));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Adding_exif_twice_replaces_the_old_block()
    {
        var png = ExifMetadata.AddToPng(Png(TestImages.Photo(8, 8)), CameraExif(true));
        var second = ExifMetadata.Sanitize(CameraExif(false), 8, 8)!;
        var twice = ExifMetadata.AddToPng(png, second);

        Assert.Equal(second, ExifMetadata.Extract(twice));
        Assert.Equal(1, Count(twice, "eXIf"u8));
    }

    [Fact]
    public void Garbage_is_ignored_rather_than_trusted()
    {
        var exif = CameraExif(bigEndian: false);
        for (var length = 0; length < exif.Length; length += 3)
        {
            _ = ExifMetadata.Sanitize(exif.AsSpan(0, length), 10, 10); // must not throw
        }

        var random = new Random(5);
        for (var i = 0; i < 300; i++)
        {
            var mutated = (byte[])exif.Clone();
            mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            _ = ExifMetadata.Sanitize(mutated, 10, 10);
        }

        Assert.Null(ExifMetadata.Sanitize("not a tiff at all"u8, 1, 1));
        Assert.Throws<InvalidImageException>(() => ExifMetadata.AddToPng("nope"u8, exif));
    }

    /// <summary>A camera-style EXIF block: IFD0 with an Exif IFD, a GPS IFD and a thumbnail IFD.</summary>
    private static byte[] CameraExif(bool bigEndian)
    {
        var w = new TiffBuilder(bigEndian);
        var ifd0 = w.NewIfd();
        ifd0.Ascii(Make, "Canon");
        ifd0.Ascii(Model, "EOS R6 Mark II");
        ifd0.Short(Orientation, 6);
        ifd0.Rational(XResolution, 72, 1);
        var exif = ifd0.SubIfd(ExifIfd);
        exif.Rational(ExposureTime, 1, 250);
        exif.Ascii(DateTimeOriginal, "2026:11:02 10:00:00");
        exif.Undefined(MakerNote, Encoding.ASCII.GetBytes("SECRET-SERIAL-12345"));
        exif.Long(PixelX, 4000);
        exif.Long(PixelY, 3000);
        var gps = ifd0.SubIfd(GpsIfd);
        gps.Ascii(0x0001, "N");
        gps.Rationals(0x0002, (13, 1), (45, 1), (30, 1));
        var thumbnail = w.NewIfd();
        thumbnail.Undefined(0x0201, Encoding.ASCII.GetBytes("THUMBNAIL-JPEG-BYTES"));
        return w.Build(ifd0, thumbnail);
    }

    private static ReadOnlySpan<byte> GpsDegreesLittleEndian => [0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

    private static byte[] Png(PixelBuffer image)
    {
        using var stream = new MemoryStream();
        new SkiaImageCodec().EncodePng(image, stream);
        return stream.ToArray();
    }

    private static byte[] JpegWithExif(byte[] tiff)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        stream.Write("JFIF\0"u8);
        stream.Write(new byte[9]);
        var length = (ushort)(2 + 6 + tiff.Length);
        stream.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        stream.Write("Exif\0\0"u8);
        stream.Write(tiff);
        stream.Write([0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9]);
        return stream.ToArray();
    }

    private static byte[] WebPWithExif(byte[] tiff)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        body.Write("VP8X"u8);
        body.Write([10, 0, 0, 0, 0x08, 0, 0, 0, 7, 0, 0, 7, 0, 0]);
        body.Write("EXIF"u8);
        body.Write(BitConverter.GetBytes(tiff.Length));
        body.Write(tiff);
        if (tiff.Length % 2 == 1)
        {
            body.WriteByte(0);
        }

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes((int)body.Length));
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static int Count(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle); i >= 0; i = haystack[(i + 1)..].IndexOf(needle) is var next and >= 0 ? i + 1 + next : -1)
        {
            count++;
        }

        return count;
    }

    private static string Ascii(byte[] value) => Encoding.ASCII.GetString(value).TrimEnd('\0');

    private static (uint, uint) Rational(byte[] value) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(value), BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4)));

    /// <summary>Reads the little-endian TIFF the sanitizer writes: IFD0 and the Exif IFD, tag → value bytes.</summary>
    private static (Dictionary<ushort, byte[]> Ifd0, Dictionary<ushort, byte[]> Exif) Parse(byte[] tiff)
    {
        Assert.True(tiff.AsSpan().StartsWith("II*\0"u8));
        var ifd0 = ReadIfd(tiff, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)), out var next);
        Assert.Equal(0u, next);
        var exif = ifd0.TryGetValue(ExifIfd, out var pointer)
            ? ReadIfd(tiff, BinaryPrimitives.ReadUInt32LittleEndian(pointer), out _)
            : [];
        return (ifd0, exif);
    }

    private static Dictionary<ushort, byte[]> ReadIfd(byte[] tiff, uint offset, out uint next)
    {
        var result = new Dictionary<ushort, byte[]>();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)offset));
        ushort previous = 0;
        for (var i = 0; i < count; i++)
        {
            var at = (int)offset + 2 + (12 * i);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at));
            Assert.True(tag > previous, "tags must be sorted");
            previous = tag;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at + 2));
            var n = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at + 4));
            var size = type switch { 3 => 2, 4 => 4, 5 or 10 => 8, _ => 1 } * (int)n;
            var valueAt = size <= 4 ? at + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at + 8));
            result[tag] = tiff.AsSpan(valueAt, size).ToArray();
        }

        next = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)offset + 2 + (12 * count)));
        return result;
    }

    /// <summary>Just enough of a TIFF writer to build test EXIF blocks in either byte order.</summary>
    private sealed class TiffBuilder(bool bigEndian)
    {
        public Ifd NewIfd() => new(this);

        public byte[] Build(Ifd first, Ifd? second)
        {
            var output = new List<byte>(bigEndian ? "MM\0*"u8.ToArray() : "II*\0"u8.ToArray());
            output.AddRange(U32(8));
            var nextPointer = first.WriteTo(output);
            if (second is not null)
            {
                Patch(output, nextPointer, (uint)output.Count);
                second.WriteTo(output);
            }

            return [.. output];
        }

        internal byte[] U16(int value)
        {
            var b = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
            }

            return b;
        }

        internal byte[] U32(uint value)
        {
            var b = new byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            }

            return b;
        }

        internal void Patch(List<byte> output, int at, uint value)
        {
            var bytes = U32(value);
            for (var i = 0; i < 4; i++)
            {
                output[at + i] = bytes[i];
            }
        }

        internal sealed class Ifd(TiffBuilder owner)
        {
            private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Value, Ifd? Sub)> _entries = [];

            public void Ascii(ushort tag, string text) => Add(tag, 2, Encoding.ASCII.GetBytes(text + "\0"));

            public void Short(ushort tag, int value) => Add(tag, 3, owner.U16(value), 1);

            public void Long(ushort tag, uint value) => Add(tag, 4, owner.U32(value), 1);

            public void Rational(ushort tag, uint numerator, uint denominator) => Rationals(tag, (numerator, denominator));

            public void Rationals(ushort tag, params (uint N, uint D)[] values) =>
                Add(tag, 5, [.. values.SelectMany(v => owner.U32(v.N).Concat(owner.U32(v.D)))], (uint)values.Length);

            public void Undefined(ushort tag, byte[] value) => Add(tag, 7, value);

            public Ifd SubIfd(ushort tag)
            {
                var sub = new Ifd(owner);
                _entries.Add((tag, 4, 1, new byte[4], sub));
                return sub;
            }

            /// <summary>Appends this IFD (and its sub-IFDs); returns where its next-IFD pointer sits.</summary>
            public int WriteTo(List<byte> output)
            {
                var start = output.Count;
                var sorted = _entries.OrderBy(e => e.Tag).ToList();
                var dataAt = start + 2 + (12 * sorted.Count) + 4;
                var data = new List<byte>();
                var pointers = new List<(int At, Ifd Sub)>();
                output.AddRange(owner.U16(sorted.Count));
                foreach (var (tag, type, count, value, sub) in sorted)
                {
                    output.AddRange(owner.U16(tag));
                    output.AddRange(owner.U16(type));
                    output.AddRange(owner.U32(count == 0 ? (uint)value.Length : count));
                    if (sub is not null)
                    {
                        pointers.Add((output.Count, sub));
                        output.AddRange(new byte[4]);
                    }
                    else if (value.Length <= 4)
                    {
                        output.AddRange(value.Concat(new byte[4 - value.Length]));
                    }
                    else
                    {
                        output.AddRange(owner.U32((uint)(dataAt + data.Count)));
                        data.AddRange(value);
                        if (data.Count % 2 == 1)
                        {
                            data.Add(0);
                        }
                    }
                }

                var next = output.Count;
                output.AddRange(new byte[4]);
                output.AddRange(data);
                foreach (var (at, sub) in pointers)
                {
                    owner.Patch(output, at, (uint)output.Count);
                    sub.WriteTo(output);
                }

                return next;
            }

            private void Add(ushort tag, ushort type, byte[] value, uint count = 0) => _entries.Add((tag, type, count, value, null));
        }
    }
}
