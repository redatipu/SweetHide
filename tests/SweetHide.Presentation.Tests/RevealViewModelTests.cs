using SweetHide.Core.Payloads;
using SweetHide.Presentation.ViewModels;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public sealed class RevealViewModelTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-reveal-");
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly RevealViewModel _vm;

    public RevealViewModelTests() =>
        _vm = new RevealViewModel(Engine(), Loader(), _dialogs, _clipboard, new FakeShell());

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Reveals_messages_and_files()
    {
        await _vm.LoadImageFromBytesAsync("stego.png", StegoPng(
            PayloadEntry.FromText("ข้อความลับ"),
            PayloadEntry.FromFile("report.pdf", "pdf-bytes"u8)));
        Assert.False(_vm.RevealCommand.CanExecute(null));

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Success, _vm.StatusSeverity);
        Assert.Equal(2, _vm.Items.Count);
        Assert.True(_vm.Items[0].IsText);
        Assert.Equal("ข้อความลับ", _vm.Items[0].Text);
        Assert.True(_vm.Items[1].IsFile);
        Assert.Equal("report.pdf", _vm.Items[1].Title);
        Assert.True(_vm.HasFiles);
        Assert.Empty(_vm.Password);

        _vm.CopyTextCommand.Execute(_vm.Items[0]);
        Assert.Equal("ข้อความลับ", _clipboard.Text);
    }

    [Fact]
    public async Task Wrong_password_warns_and_keeps_the_password_for_editing()
    {
        await _vm.LoadImageFromBytesAsync("stego.png", StegoPng(PayloadEntry.FromText("x")));
        _vm.Password = "wrong";

        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Warning, _vm.StatusSeverity);
        Assert.Equal(Strings.RevealNotFound, _vm.StatusMessage);
        Assert.Empty(_vm.Items);
        Assert.Equal("wrong", _vm.Password);
    }

    [Fact]
    public async Task Save_all_keeps_hostile_names_inside_the_folder()
    {
        await _vm.LoadImageFromBytesAsync("stego.png", StegoPng(
            new PayloadEntry(PayloadEntryKind.File, "../../escape.txt", "a"u8.ToArray()),
            new PayloadEntry(PayloadEntryKind.File, "CON", "b"u8.ToArray()),
            PayloadEntry.FromFile("same.txt", "1"u8),
            PayloadEntry.FromFile("same.txt", "2"u8)));
        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        var target = _dir.CreateSubdirectory("inner");
        _dialogs.FolderPath = target.FullName;
        await _vm.SaveAllFilesCommand.ExecuteAsync(null);

        var names = target.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["_CON", "escape.txt", "same (1).txt", "same.txt"], names);
        Assert.Single(_dir.GetDirectories());
        Assert.Empty(_dir.GetFiles());
        Assert.Equal(target.FullName, _vm.StatusActionPath);
    }

    [Fact]
    public async Task Save_single_file_uses_a_save_dialog_or_a_folder_for_names_without_extension()
    {
        await _vm.LoadImageFromBytesAsync("stego.png", StegoPng(
            PayloadEntry.FromFile("photo.jpg", "jpg"u8),
            PayloadEntry.FromFile("README", "readme"u8)));
        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        _dialogs.SavePath = Path.Combine(_dir.FullName, "chosen.jpg");
        await _vm.SaveFileCommand.ExecuteAsync(_vm.Items[0]);
        Assert.Equal("photo.jpg", _dialogs.LastSuggestedName);
        Assert.Equal("jpg", File.ReadAllText(_dialogs.SavePath));

        _dialogs.FolderPath = _dir.FullName;
        await _vm.SaveFileCommand.ExecuteAsync(_vm.Items[1]);
        Assert.Equal("readme", File.ReadAllText(Path.Combine(_dir.FullName, "README")));
    }

    [Fact]
    public async Task Loading_another_image_clears_results()
    {
        await _vm.LoadImageFromBytesAsync("stego.png", StegoPng(PayloadEntry.FromText("x")));
        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.True(_vm.HasResults);

        await _vm.LoadImageFromBytesAsync("other.png", PngBytes(40, 40));
        Assert.False(_vm.HasResults);
        Assert.Equal("40 × 40 · other.png", _vm.ImageInfo);
    }

    private static byte[] StegoPng(params PayloadEntry[] entries)
    {
        using var input = new MemoryStream(PngBytes(160, 120));
        var stego = Engine().Embed(Codec.Decode(input, false), new Payload(entries), Password);
        using var output = new MemoryStream();
        Codec.EncodePng(stego, output);
        return output.ToArray();
    }
}
