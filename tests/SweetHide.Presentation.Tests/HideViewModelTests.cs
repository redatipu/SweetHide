using SweetHide.Core;
using SweetHide.Presentation.ViewModels;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public sealed class HideViewModelTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-gui-");
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeShell _shell = new();
    private readonly HideViewModel _vm;

    public HideViewModelTests() =>
        _vm = new HideViewModel(Engine(), Codec, Loader(), _dialogs, _clipboard, _shell, TimeSpan.Zero);

    public void Dispose()
    {
        _vm.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task Capacity_follows_cover_content_and_bits()
    {
        Assert.Equal(CapacityState.NoImage, _vm.CapacityState);
        Assert.False(_vm.HideCommand.CanExecute(null));

        var png = PngBytes(200, 100);
        await _vm.LoadCoverFromBytesAsync("photo.png", png);
        Assert.True(_vm.HasCover);
        Assert.Equal("200 × 100 · photo.png", _vm.CoverInfo);
        Assert.Equal(CapacityState.Empty, _vm.CapacityState);
        var pixels = Codec.Decode(new MemoryStream(png), applyOrientation: true);
        Assert.Equal(StegoEngine.GetCapacity(pixels, 1).ContainerBytes, _vm.CapacityBytes);
        Assert.InRange(_vm.CapacityBytes, 7000, 200 * 100 * 3 / 8);

        _vm.MessageText = "สวัสดี";
        Assert.Equal(CapacityState.Measuring, _vm.CapacityState);
        await _vm.MeasureTask;
        Assert.Equal(CapacityState.Ok, _vm.CapacityState);
        Assert.InRange(_vm.RequiredBytes!.Value, StegoEngine.ContainerOverhead, 100);

        _vm.BitsIndex = 1;
        Assert.Equal(2, _vm.BitsPerChannel);
        Assert.Equal(StegoEngine.GetCapacity(pixels, 2).ContainerBytes, _vm.CapacityBytes);
    }

    [Fact]
    public async Task Warns_above_half_and_blocks_when_over_capacity()
    {
        await _vm.LoadCoverFromBytesAsync("small.png", PngBytes(60, 60)); // ~1.3 KiB at 1 bit
        var capacity = _vm.CapacityBytes;

        await AddAttachmentAsync("a.bin", RandomBytes((int)(capacity * 0.7)));
        Assert.Equal(CapacityState.Warning, _vm.CapacityState);
        Assert.True(_vm.IsCapacityWarning);
        Assert.True(_vm.HasCapacityHint);

        await AddAttachmentAsync("b.bin", RandomBytes((int)capacity, seed: 8));
        Assert.Equal(CapacityState.Over, _vm.CapacityState);
        Assert.True(_vm.IsCapacityOver);
        Assert.Equal(100, _vm.UsagePercent);

        _vm.Password = _vm.ConfirmPassword = Password;
        Assert.False(_vm.HideCommand.CanExecute(null));

        _vm.RemoveAttachmentCommand.Execute(_vm.Attachments[1]);
        await _vm.MeasureTask;
        Assert.True(_vm.HideCommand.CanExecute(null));
    }

    [Fact]
    public async Task Requires_matching_passwords()
    {
        await _vm.LoadCoverFromBytesAsync("c.png", PngBytes(100, 100));
        _vm.MessageText = "hi";
        await _vm.MeasureTask;

        _vm.Password = Password;
        Assert.False(_vm.HideCommand.CanExecute(null));
        Assert.False(_vm.ShowPasswordMismatch); // nothing typed in the confirm box yet

        _vm.ConfirmPassword = Password + "x";
        Assert.True(_vm.ShowPasswordMismatch);
        Assert.False(_vm.HideCommand.CanExecute(null));

        _vm.ConfirmPassword = Password;
        Assert.True(_vm.HideCommand.CanExecute(null));
        Assert.NotEmpty(_vm.StrengthText);
    }

    [Fact]
    public async Task Hide_saves_a_png_that_reveals_the_message_and_files()
    {
        await _vm.LoadCoverFromBytesAsync("holiday.jpg", PngBytes(300, 200));
        _vm.MessageText = "บรรทัดแรก\rบรรทัดสอง"; // WinUI TextBox line breaks
        var file = Path.Combine(_dir.FullName, "plan.pdf");
        File.WriteAllBytes(file, RandomBytes(2000));
        await _vm.AddFilesFromPathsAsync([file]);
        await _vm.MeasureTask;
        _vm.Password = _vm.ConfirmPassword = Password;
        _dialogs.SavePath = Path.Combine(_dir.FullName, "holiday"); // ".png" gets added

        await _vm.HideCommand.ExecuteAsync(null);

        Assert.Equal("holiday.png", _dialogs.LastSuggestedName);
        Assert.Equal(StatusSeverity.Success, _vm.StatusSeverity);
        Assert.True(_vm.StatusIsOpen);
        var saved = Path.Combine(_dir.FullName, "holiday.png");
        Assert.Equal(saved, _vm.LastSavedPath);
        Assert.Equal(saved, _vm.StatusActionPath);
        Assert.Empty(_vm.Password);
        Assert.Empty(_vm.ConfirmPassword);

        using var stream = File.OpenRead(saved);
        var payload = Engine().Extract(Codec.Decode(stream, applyOrientation: false), Password);
        Assert.Equal("บรรทัดแรก\nบรรทัดสอง", payload.Entries[0].GetText());
        Assert.Equal("plan.pdf", payload.Entries[1].Name);
        Assert.Equal(File.ReadAllBytes(file), payload.Entries[1].Content.ToArray());

        _vm.ShowInFolderCommand.Execute(null);
        Assert.Equal([saved], _shell.Shown);
    }

    [Fact]
    public async Task Cancelled_save_dialog_changes_nothing()
    {
        await _vm.LoadCoverFromBytesAsync("c.png", PngBytes(80, 80));
        _vm.MessageText = "x";
        await _vm.MeasureTask;
        _vm.Password = _vm.ConfirmPassword = Password;
        _dialogs.SavePath = null;

        await _vm.HideCommand.ExecuteAsync(null);

        Assert.False(_vm.StatusIsOpen);
        Assert.Equal(Password, _vm.Password);
        Assert.Empty(_dir.GetFiles());
    }

    [Fact]
    public async Task Paste_without_an_image_warns()
    {
        await _vm.PasteCoverCommand.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Warning, _vm.StatusSeverity);
        Assert.False(_vm.HasCover);

        _clipboard.Image = new("clip.png", PngBytes(50, 50));
        await _vm.PasteCoverCommand.ExecuteAsync(null);
        Assert.True(_vm.HasCover);
        Assert.False(_vm.StatusIsOpen);
    }

    [Fact]
    public async Task Broken_image_shows_an_error_and_keeps_the_previous_cover()
    {
        await _vm.LoadCoverFromBytesAsync("good.png", PngBytes(50, 50));
        await _vm.LoadCoverFromBytesAsync("bad.png", [1, 2, 3]);

        Assert.Equal(StatusSeverity.Error, _vm.StatusSeverity);
        Assert.Equal("good.png", _vm.Cover!.Name);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task Missing_files_are_skipped()
    {
        await _vm.AddFilesFromPathsAsync([Path.Combine(_dir.FullName, "does-not-exist.txt")]);
        Assert.Empty(_vm.Attachments);
        Assert.True(_vm.HasNoAttachments);
    }

    private async Task AddAttachmentAsync(string name, byte[] content)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, content);
        await _vm.AddFilesFromPathsAsync([path]);
        await _vm.MeasureTask;
    }
}
