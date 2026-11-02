using SweetHide.Core.Imaging;

namespace SweetHide.Core.Tests;

public sealed class SafeFileWriterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sweethide-writer-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Writes_inside_the_folder_and_never_overwrites_by_default()
    {
        var target = Path.Combine(_root.FullName, "out");
        var first = SafeFileWriter.Write(target, "../../note.txt", "a"u8);
        var second = SafeFileWriter.Write(target, "note.txt", "b"u8);

        Assert.Equal(Path.Combine(target, "note.txt"), first);
        Assert.Equal(Path.Combine(target, "note (1).txt"), second);
        Assert.Equal("a", File.ReadAllText(first));
        Assert.Equal("b", File.ReadAllText(second));
        Assert.Empty(_root.GetFiles());
    }

    [Fact]
    public void Overwrite_replaces_the_existing_file()
    {
        var path = SafeFileWriter.Write(_root.FullName, "x.bin", "old"u8);
        Assert.Equal(path, SafeFileWriter.Write(_root.FullName, "x.bin", "new"u8, overwrite: true));
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(5L * 1024 * 1024, "5.0 MiB")]
    public void ByteSize_formats_binary_units(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void SavePng_replaces_atomically_and_leaves_no_temp_files()
    {
        var codec = new SkiaImageCodec();
        var path = Path.Combine(_root.FullName, "img.png");
        File.WriteAllText(path, "existing");

        Assert.Throws<IOException>(() => codec.SavePng(TestImages.Photo(10, 10), path, overwrite: false));
        Assert.Equal("existing", File.ReadAllText(path));

        codec.SavePng(TestImages.Photo(10, 10), path, overwrite: true);
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(10, codec.Decode(stream, false).Width);
        }

        Assert.Single(_root.GetFiles());
    }
}
