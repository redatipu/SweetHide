using System.CommandLine;
using System.Text.Json;

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
    private readonly MemoryStream _stdout = new();
    private byte[] _stdin = [];

    public void Dispose()
    {
        _stdout.Dispose();
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
    [InlineData("out.gif")]
    public void Refuses_lossy_output(string name)
    {
        Assert.Equal(1, Run("hide", "-i", WriteCover("c.png", 50, 50), "-o", Path(_dir, name), "-t", "x"));
        Assert.Contains("lossless", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("out.webp")]
    [InlineData("out.bmp")]
    [InlineData("out.tiff")]
    public void Writes_and_reveals_every_lossless_format(string name)
    {
        var output = Path(_dir, name);
        Assert.Equal(0, Run("hide", "-i", WriteCover("c.png", 90, 70), "-o", output, "-t", "lossless"));
        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", output));
        Assert.Equal("lossless", _out.ToString().TrimEnd());
    }

    [Fact]
    public void Batch_format_option_picks_the_extension()
    {
        var outDir = _dir.CreateSubdirectory("webp");
        Assert.Equal(0, Run("hide", "-i", WriteCover("a.png", 60, 60), "--output-dir", outDir.FullName, "--format", "WebP", "-t", "x"));
        Assert.True(File.Exists(Path(outDir, "a.webp")));
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

    [Fact]
    public void Json_output_for_hide_reveal_capacity_and_analyze()
    {
        var cover = WriteCover("cover.png", 120, 90);
        var output = Path(_dir, "out.png");

        Assert.Equal(0, Run("hide", "-i", cover, "-o", output, "-t", "สวัสดี", "--json"));
        using (var hide = JsonDocument.Parse(_out.ToString()))
        {
            var report = hide.RootElement[0];
            Assert.Equal(output, report.GetProperty("output").GetString());
            Assert.Equal(1, report.GetProperty("messages").GetInt32());
            Assert.True(report.GetProperty("psnr").GetDouble() > 40);
        }

        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", output, "--json", "--list"));
        using (var reveal = JsonDocument.Parse(_out.ToString()))
        {
            Assert.Equal("สวัสดี", reveal.RootElement[0].GetProperty("messages")[0].GetString());
        }

        ResetOutput();
        Assert.Equal(0, Run("capacity", "-i", cover, "-i", output, "--json"));
        using (var capacity = JsonDocument.Parse(_out.ToString()))
        {
            Assert.Equal(2, capacity.RootElement.GetArrayLength());
            Assert.Equal(4, capacity.RootElement[0].GetProperty("capacity").GetArrayLength());
        }

        ResetOutput();
        Assert.Equal(0, Run("analyze", "-i", output, "--json"));
        using var analyze = JsonDocument.Parse(_out.ToString());
        Assert.Equal(3, analyze.RootElement[0].GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public void Json_errors_are_machine_readable()
    {
        var output = HideSample();
        _passwords.Value = "wrong";
        Assert.Equal(2, Run("reveal", "-i", output, "--json"));
        using var error = JsonDocument.Parse(_out.ToString());
        Assert.Equal(2, error.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Cover_from_stdin_and_png_to_stdout()
    {
        _stdin = File.ReadAllBytes(WriteCover("c.png", 100, 80));
        Assert.Equal(0, Run("hide", "-i", "-", "-o", "-", "-t", "piped"));
        Assert.Empty(_out.ToString()); // stdout carried only the PNG
        Assert.Contains("Wrote stdout", _err.ToString(), StringComparison.Ordinal);

        _stdin = _stdout.ToArray();
        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", "-"));
        Assert.Equal("piped", _out.ToString().TrimEnd());
    }

    [Fact]
    public void Message_from_stdin()
    {
        _stdin = "จากไฟล์ที่ pipe เข้ามา\n"u8.ToArray();
        var output = Path(_dir, "o.png");
        Assert.Equal(0, Run("hide", "-i", WriteCover("c.png", 100, 80), "-o", output, "--text-file", "-"));
        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", output));
        Assert.Equal("จากไฟล์ที่ pipe เข้ามา", _out.ToString().TrimEnd());
    }

    [Fact]
    public void Only_one_thing_may_read_stdin()
    {
        Assert.Equal(1, Run("hide", "-i", "-", "--text-file", "-", "-o", Path(_dir, "o.png")));
        Assert.Contains("standard input", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Run("reveal", "-i", "-", "--password-stdin"));
    }

    [Fact]
    public void Batch_hide_and_reveal()
    {
        var a = WriteCover("a.png", 90, 90);
        var b = WriteCover("b.png", 100, 70);
        var outDir = _dir.CreateSubdirectory("batch");

        Assert.Equal(0, Run("hide", "-i", a, "-i", b, "--output-dir", outDir.FullName, "-t", "same note", "-f", WriteFile("n.txt", [1, 2, 3])));
        Assert.True(File.Exists(Path(outDir, "a.png")));
        Assert.True(File.Exists(Path(outDir, "b.png")));
        Assert.Equal(1, _passwords.Calls); // asked once for the whole batch

        var revealDir = _dir.CreateSubdirectory("revealed");
        ResetOutput();
        Assert.Equal(0, Run("reveal", "-i", Path(outDir, "a.png"), "-i", Path(outDir, "b.png"), "-o", revealDir.FullName));
        Assert.True(File.Exists(System.IO.Path.Combine(revealDir.FullName, "a", "n.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(revealDir.FullName, "b", "n.txt")));
        Assert.Contains("== ", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_covers_need_an_output_folder()
    {
        Assert.Equal(1, Run("hide", "-i", WriteCover("a.png", 50, 50), "-i", WriteCover("b.png", 50, 50), "-o", Path(_dir, "x.png"), "-t", "x"));
        Assert.Contains("--output-dir", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_are_hidden_as_zip()
    {
        var folder = _dir.CreateSubdirectory("notes");
        File.WriteAllText(System.IO.Path.Combine(folder.FullName, "one.txt"), "1");
        folder.CreateSubdirectory("sub").CreateSubdirectory("deep");
        File.WriteAllText(System.IO.Path.Combine(folder.FullName, "sub", "deep", "two.txt"), "2");
        var output = Path(_dir, "o.png");

        Assert.Equal(0, Run("hide", "-i", WriteCover("c.png", 200, 200), "-o", output, "-f", folder.FullName));
        var revealDir = _dir.CreateSubdirectory("r");
        Assert.Equal(0, Run("reveal", "-i", output, "-o", revealDir.FullName));

        using var zip = System.IO.Compression.ZipFile.OpenRead(Path(revealDir, "notes.zip"));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("deep/two.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bash", "complete -o filenames -F _sweethide sweethide")]
    [InlineData("zsh", "bashcompinit")]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    public void Completion_scripts_list_commands_and_options(string shell, string expected)
    {
        Assert.Equal(0, Run("completion", shell));
        var script = _out.ToString();
        Assert.Contains(expected, script, StringComparison.Ordinal);
        Assert.Contains("reveal", script, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", script, StringComparison.Ordinal);
        Assert.Contains("--json", script, StringComparison.Ordinal);
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
        var context = new CliContext
        {
            Out = _out,
            Error = _err,
            Passwords = _passwords,
            Kdf = FastKdf,
            OpenStandardInput = () => new MemoryStream(_stdin),
            OpenStandardOutput = () => _stdout,
        };
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
