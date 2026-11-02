using System.CommandLine;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private const string Password = "homelab-test-pass";
    private static readonly KdfParameters FastKdf = new(64, 1, 1);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-cli-");
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly FakePasswords _passwords = new(Password);
    private readonly SkiaImageCodec _codec = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public void Hide_then_reveal_roundtrips_messages_and_files()
    {
        var cover = WriteCover("cover.png", 200, 150);
        var secretFile = WriteFile("แผนงาน.txt", "รายละเอียดลับ"u8.ToArray());
        var output = Path(_dir, "stego.png");

        Assert.Equal(0, Run("hide", "-i", cover, "-o", output, "-t", "สวัสดี homelab", "-f", secretFile));
        Assert.Contains("Hidden 1 message(s) and 1 file(s)", _out.ToString(), StringComparison.Ordinal);
        Assert.True(_passwords.LastConfirm);

        var revealDir = _dir.CreateSubdirectory("revealed");
        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", output, "-o", revealDir.FullName));
        Assert.Equal("สวัสดี homelab", _out.ToString().TrimEnd());
        Assert.Equal("รายละเอียดลับ"u8.ToArray(), File.ReadAllBytes(Path(revealDir, "แผนงาน.txt")));
        Assert.False(_passwords.LastConfirm);
    }

    [Fact]
    public void Reveal_list_writes_nothing()
    {
        var output = HideSample();
        var revealDir = _dir.CreateSubdirectory("list");
        ResetOutput();

        Assert.Equal(0, Run("reveal", "-i", output, "-o", revealDir.FullName, "--list"));
        Assert.Contains("[1] message", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("[2] file \"data.bin\"", _out.ToString(), StringComparison.Ordinal);
        Assert.Empty(revealDir.GetFiles());
    }

    [Fact]
    public void Reveal_does_not_overwrite_existing_files()
    {
        var output = HideSample();
        var revealDir = _dir.CreateSubdirectory("existing");
        File.WriteAllText(Path(revealDir, "data.bin"), "keep me");

        Assert.Equal(0, Run("reveal", "-i", output, "-o", revealDir.FullName));
        Assert.Equal("keep me", File.ReadAllText(Path(revealDir, "data.bin")));
        Assert.True(File.Exists(Path(revealDir, "data (1).bin")));
    }

    [Fact]
    public void Wrong_password_exits_with_code_2()
    {
        var output = HideSample();
        _passwords.Value = "not the password";
        Assert.Equal(2, Run("reveal", "-i", output));
        Assert.Contains("wrong", _err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capacity_problem_exits_with_code_3_before_asking_for_a_password()
    {
        var cover = WriteCover("small.png", 30, 30);
        var big = new byte[5000];
        new Random(1).NextBytes(big);
        var bigFile = WriteFile("big.bin", big);

        Assert.Equal(3, Run("hide", "-i", cover, "-o", Path(_dir, "out.png"), "-f", bigFile));
        Assert.Equal(0, _passwords.Calls);
        Assert.Contains("larger image", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Suggests_more_bits_when_that_would_fit()
    {
        var cover = WriteCover("medium.png", 60, 60); // ~1.3 KiB at 1 bit, ~2.6 KiB at 2 bits
        var data = new byte[2000];
        new Random(2).NextBytes(data);

        Assert.Equal(3, Run("hide", "-i", cover, "-o", Path(_dir, "out.png"), "-f", WriteFile("d.bin", data)));
        Assert.Contains("--bits 2", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("out.jpg")]
    [InlineData("out.jpeg")]
    [InlineData("out.webp")]
    public void Refuses_lossy_or_non_png_output(string name)
    {
        Assert.Equal(1, Run("hide", "-i", WriteCover("c.png", 50, 50), "-o", Path(_dir, name), "-t", "x"));
        Assert.Contains(".png", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_overwrite_output_without_force()
    {
        var cover = WriteCover("c.png", 80, 80);
        var output = WriteFile("exists.png", [1, 2, 3]);

        Assert.Equal(1, Run("hide", "-i", cover, "-o", output, "-t", "x"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));

        Assert.Equal(0, Run("hide", "-i", cover, "-o", output, "-t", "x", "--force"));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Requires_something_to_hide()
    {
        Assert.Equal(1, Run("hide", "-i", WriteCover("c.png", 50, 50), "-o", Path(_dir, "o.png")));
        Assert.Contains("Nothing to hide", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Malicious_file_names_cannot_escape_the_output_folder()
    {
        // Build a payload with hostile names directly (the CLI would never create these).
        var payload = new Payload(
        [
            new PayloadEntry(PayloadEntryKind.File, "../../escape.txt", "a"u8.ToArray()),
            new PayloadEntry(PayloadEntryKind.File, @"..\..\escape2.txt", "b"u8.ToArray()),
            new PayloadEntry(PayloadEntryKind.File, "/etc/cron.d/evil", "c"u8.ToArray()),
            new PayloadEntry(PayloadEntryKind.File, "..", "d"u8.ToArray()),
            new PayloadEntry(PayloadEntryKind.File, "NUL", "e"u8.ToArray()),
        ]);
        var stego = new StegoEngine(FastKdf).Embed(Decode(WriteCover("c.png", 120, 120)), payload, Password);
        var stegoPath = Path(_dir, "hostile.png");
        using (var stream = File.Create(stegoPath))
        {
            _codec.EncodePng(stego, stream);
        }

        var revealDir = _dir.CreateSubdirectory("jail").CreateSubdirectory("inner");
        Assert.Equal(0, Run("reveal", "-i", stegoPath, "-o", revealDir.FullName));

        var written = revealDir.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["_NUL", "escape.txt", "escape2.txt", "evil", "file"], written);
        Assert.DoesNotContain(_dir.GetFiles("escape*", SearchOption.AllDirectories), f => f.Directory!.FullName != revealDir.FullName);
    }

    [Fact]
    public void Capacity_command_lists_every_bit_depth()
    {
        Assert.Equal(0, Run("capacity", "-i", WriteCover("c.png", 1920, 1080)));
        var output = _out.ToString();
        Assert.Contains("1920 x 1080", output, StringComparison.Ordinal);
        Assert.Contains("759.3 KiB", output, StringComparison.Ordinal); // (1920*1080*3 - 128) / 8 - 36 bytes
        Assert.Contains("visible noise", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_command_reports_channels_and_writes_bit_plane()
    {
        var plane = Path(_dir, "plane.png");
        Assert.Equal(0, Run("analyze", "-i", WriteCover("c.png", 64, 64), "--bit-plane", plane));
        Assert.Contains("p(LSB replacement)", _out.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(plane));
    }

    [Fact]
    public void Invalid_bits_value_is_a_usage_error()
    {
        Assert.NotEqual(0, Run("hide", "-i", WriteCover("c.png", 50, 50), "-o", Path(_dir, "o.png"), "-t", "x", "--bits", "9"));
        Assert.Equal(0, _passwords.Calls);
    }

    [Fact]
    public void Garbage_input_image_is_reported_cleanly()
    {
        Assert.Equal(1, Run("reveal", "-i", WriteFile("not-an-image.png", [1, 2, 3, 4])));
        Assert.StartsWith("error:", _err.ToString(), StringComparison.Ordinal);
    }

    private string HideSample()
    {
        var output = Path(_dir, $"sample-{Guid.NewGuid():N}.png");
        var data = WriteFile("data.bin", [9, 8, 7]);
        Assert.Equal(0, Run("hide", "-i", WriteCover($"cover-{Guid.NewGuid():N}.png", 120, 90), "-o", output, "-t", "hi", "-f", data));
        ResetOutput();
        return output;
    }

    private int Run(params string[] args)
    {
        var context = new CliContext { Out = _out, Error = _err, Passwords = _passwords, Kdf = FastKdf };
        return CliApp.Create(context).Parse(args).Invoke(new InvocationConfiguration { Output = _out, Error = _err });
    }

    private void ResetOutput()
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
    }

    private string WriteCover(string name, int width, int height)
    {
        var random = new Random(width * height);
        var data = new byte[width * height * 4];
        random.NextBytes(data);
        for (var i = 3; i < data.Length; i += 4)
        {
            data[i] = 255;
        }

        var path = Path(_dir, name);
        using var stream = File.Create(path);
        _codec.EncodePng(new PixelBuffer(width, height, data), stream);
        return path;
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private PixelBuffer Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return _codec.Decode(stream, applyOrientation: false);
    }

    private static string Path(DirectoryInfo dir, string name) => System.IO.Path.Combine(dir.FullName, name);

    private sealed class FakePasswords(string value) : IPasswordProvider
    {
        public string Value { get; set; } = value;

        public int Calls { get; private set; }

        public bool LastConfirm { get; private set; }

        public string GetPassword(bool confirm, bool fromStdin)
        {
            Calls++;
            LastConfirm = confirm;
            return Value;
        }
    }
}
