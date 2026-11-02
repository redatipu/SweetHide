using System.Buffers.Binary;

using FsCheck;
using FsCheck.Fluent;

using SweetHide.Forensics.Containers;

using static SweetHide.Forensics.Tests.FileBuilders;
using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

/// <summary>
/// Inputs are hostile: every parser must survive truncation, lying length fields and random garbage without
/// throwing and without allocating anything proportional to a declared (rather than actual) size.
/// </summary>
public class HostileInputTests
{
    private const long AllocationBudget = 4 * 1024 * 1024;

    /// <summary>Valid starts of every format the library parses, to be truncated and corrupted.</summary>
    public static IEnumerable<byte[]> Seeds() =>
    [
        Png(8, 8, ("tEXt", TextChunk("Comment", "hello")), ("zTXt", [.. "k"u8, 0, 0, 0x78, 0x9C])),
        Jpeg((0xE1, Concat(Utf8("Exif\0\0"), Utf8("MM\0*\0\0\0\x08"))), (0xFE, Utf8("comment"))),
        Gif(["comment"], [("XMP DataXMP", Utf8("<x/>"))]),
        Riff("WEBP", ("VP8X", new byte[10]), ("EXIF", Utf8("II*\0\x08\0\0\0")), ("XMP ", Utf8("<x/>"))),
        Riff("WAVE", ("fmt ", [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0]), ("data", new byte[64])),
        Bmp(4, 4, gap: 4),
    ];

    public static TheoryData<int> SeedIndexes() => new(Enumerable.Range(0, Seeds().Count()));

    [Theory]
    [MemberData(nameof(SeedIndexes))]
    public void Every_truncation_is_handled(int seedIndex)
    {
        var seed = Seeds().ElementAt(seedIndex);
        for (var length = 0; length <= seed.Length; length++)
        {
            var report = ContainerInspector.Inspect(seed.AsMemory(0, length), cancellationToken: Ct);
            Assert.Equal(length, report.FileLength);
        }
    }

    [Fact]
    public void Huge_png_chunk_length_is_reported_not_trusted()
    {
        var png = Png();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 0xFFFFFFF0); // IHDR claims ~4 GiB

        var allocated = Allocated(() => ContainerInspector.Inspect(png, cancellationToken: Ct), out var report);

        var ihdr = Assert.Single(report.Structure, s => s.Type == "IHDR");
        Assert.True(ihdr.Traits.HasFlag(StructureTraits.Truncated));
        Assert.Equal(png.Length - 8, ihdr.Length);
        Assert.InRange(allocated, 0, AllocationBudget);
    }

    [Fact]
    public void Huge_jpeg_gif_riff_and_bmp_fields_are_reported_not_trusted()
    {
        var jpeg = Jpeg((0xFE, Utf8("comment")));
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4), 0xFFFF);
        var jpegReport = ContainerInspector.Inspect(jpeg, cancellationToken: Ct);
        Assert.True(jpegReport.Structure.Single(s => s.Type == "COM").Traits.HasFlag(StructureTraits.Truncated));

        var gif = Gif(["x"]);
        gif[^1] = 0xFF; // trailer becomes the start of an unknown block
        var gifReport = ContainerInspector.Inspect(gif, cancellationToken: Ct);
        Assert.NotEmpty(gifReport.Warnings);
        Assert.Null(gifReport.Trailing);

        var riff = Riff("WAVE", ("data", new byte[16]));
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(16), uint.MaxValue);
        var allocated = Allocated(() => ContainerInspector.Inspect(riff, cancellationToken: Ct), out var riffReport);
        Assert.True(riffReport.Structure.Single(s => s.Type == "data").Traits.HasFlag(StructureTraits.Truncated));
        Assert.InRange(allocated, 0, AllocationBudget);

        var bmp = Bmp(2, 2);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), int.MinValue);
        Assert.Contains(ContainerInspector.Inspect(bmp, cancellationToken: Ct).Warnings, w => w.Contains("Implausible", StringComparison.Ordinal));
    }

    [Fact]
    public void Many_zip_local_headers_cost_one_pass()
    {
        // 20k local-header signatures and no end record: estimating each one separately would rescan the rest of
        // the file every time.
        var file = new byte[20_000 * 30];
        for (var i = 0; i < file.Length; i += 30)
        {
            "PK\x03\x04"u8.CopyTo(file.AsSpan(i));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = SignatureScanner.Scan(file, cancellationToken: Ct);
        Assert.Single(found);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Many_fake_jpeg_headers_do_not_each_rescan_the_file()
    {
        // 256 JPEG starts, each opening a scan whose "entropy-coded data" (stuffed FF 00 pairs) runs to the end of
        // a 16 MiB file: measuring every one of them separately would read 4 GiB.
        var file = new byte[16 * 1024 * 1024];
        for (var i = 0; i < file.Length; i += 2)
        {
            file[i] = 0xFF;
        }

        byte[] header = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02];
        for (var i = 0; i < 256; i++)
        {
            header.CopyTo(file, i * 1024);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = SignatureScanner.Scan(file, cancellationToken: Ct);

        // Unbounded this took ~30 s in a Debug build; bounded it takes ~1 s, so 10 s leaves room for loaded CI runners.
        Assert.Equal(256, found.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var seeds = Seeds().ToArray();
        var gen =
            from index in Gen.Choose(0, seeds.Length - 1)
            from flips in Gen.ListOf(Gen.Zip(Gen.Choose(0, 4096), Gen.Choose(0, 255)))
            from cut in Gen.Choose(0, 4096)
            select Mutate(seeds[index], flips, cut);

        Prop.ForAll(gen.ToArbitrary(), file =>
        {
            var report = ContainerInspector.Inspect(file, cancellationToken: Ct);
            return report.FileLength == file.Length && report.Structure.All(s => WithinFile(s, file.Length));
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Arbitrary_bytes_never_throw()
    {
        Prop.ForAll<byte[]>(bytes =>
        {
            var report = ContainerInspector.Inspect(bytes, cancellationToken: Ct);
            SignatureScanner.Scan(bytes, cancellationToken: Ct);
            return report.Structure.All(s => WithinFile(s, bytes.Length));
        }).QuickCheckThrowOnFailure();
    }

    /// <summary>Callers slice the file with these ranges, so they must always be inside it.</summary>
    internal static bool WithinFile(StructureItem item, long fileLength) =>
        item.Offset >= 0 && item.Length >= 0 && item.Offset + item.Length <= fileLength
        && item.DataOffset >= 0 && item.DataLength >= 0 && item.DataOffset + item.DataLength <= fileLength;

    internal static byte[] Mutate(byte[] seed, IEnumerable<(int Offset, int Value)> flips, int cut)
    {
        var copy = (byte[])seed.Clone();
        foreach (var (offset, value) in flips)
        {
            copy[offset % copy.Length] = (byte)value;
        }

        return copy[..Math.Min(copy.Length, Math.Max(cut, copy.Length / 2))];
    }

    internal static long Allocated<T>(Func<T> action, out T result)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        result = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
