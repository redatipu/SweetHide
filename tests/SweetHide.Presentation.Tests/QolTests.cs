using SweetHide.Core.Payloads;
using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public sealed class QolTests : IDisposable
{
    private static readonly TimeSpan ClearDelay = TimeSpan.FromMilliseconds(150);
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-qol-");
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeShell _shell = new();

    public void Dispose() => _dir.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passphrases_have_enough_entropy_and_use_the_word_list(bool thai)
    {
        Assert.True(PassphraseGenerator.EntropyBits(thai) >= PassphraseGenerator.TargetBits);
        var words = PassphraseGenerator.Generate(thai).Split('-');
        Assert.Equal(PassphraseGenerator.WordCount(thai), words.Length);
        Assert.All(words, w => Assert.Contains(w, PassphraseGenerator.Words(thai)));
        Assert.NotEqual(PassphraseGenerator.Generate(thai), PassphraseGenerator.Generate(thai));
    }

    [Fact]
    public async Task Fit_button_picks_the_smallest_bits_that_fit()
    {
        using var vm = Hide();
        await vm.LoadCoverFromBytesAsync("c.png", PngBytes(60, 60)); // ~1.3 KiB at 1 bit
        var file = Path.Combine(_dir.FullName, "d.bin");
        File.WriteAllBytes(file, RandomBytes(2000));
        await vm.AddFilesFromPathsAsync([file]);
        await vm.MeasureTask;

        Assert.True(vm.IsCapacityOver);
        Assert.Equal(2, vm.RecommendedBits);
        Assert.Contains("2", vm.FitButtonText, StringComparison.Ordinal);
        vm.FitBitsCommand.Execute(null);
        Assert.Equal(2, vm.BitsPerChannel);
        Assert.False(vm.IsCapacityOver);
        Assert.Null(vm.RecommendedBits);
    }

    [Fact]
    public async Task Generated_passphrase_fills_both_boxes_and_its_copy_is_cleared_later()
    {
        using var vm = Hide();
        vm.GeneratePassphraseCommand.Execute(null);
        Assert.True(vm.HasGeneratedPassphrase);
        Assert.Equal(vm.GeneratedPassphrase, vm.Password);
        Assert.Equal(vm.GeneratedPassphrase, vm.ConfirmPassword);
        Assert.False(vm.ShowPasswordMismatch);

        vm.CopyGeneratedPassphraseCommand.Execute(null);
        Assert.Equal(vm.GeneratedPassphrase, _clipboard.Text);
        await vm.ClipboardClearTask;
        Assert.Null(_clipboard.Text);
    }

    [Fact]
    public async Task Hide_can_remember_the_password_and_copy_the_png_as_a_file()
    {
        var vault = new ProtectedFileVault(Path.Combine(_dir.FullName, "vault.dat"), new XorProtector());
        using var vm = Hide(vault);
        await vm.LoadCoverFromBytesAsync("c.png", PngBytes(100, 100));
        vm.MessageText = "hi";
        await vm.MeasureTask;
        vm.Password = vm.ConfirmPassword = Password;
        vm.SaveToVault = true;
        vm.VaultName = "  Ploy ";
        _dialogs.SavePath = Path.Combine(_dir.FullName, "out.png");

        await vm.HideCommand.ExecuteAsync(null);
        Assert.Equal([("Ploy", Password)], vault.Entries.Select(e => (e.Name, e.Password)));

        Assert.True(vm.CopySavedFileCommand.CanExecute(null));
        await vm.CopySavedFileCommand.ExecuteAsync(null);
        Assert.Equal([_dialogs.SavePath], _clipboard.Files);
    }

    [Fact]
    public void Vault_is_stored_encrypted_and_ordered_by_last_use()
    {
        var path = Path.Combine(_dir.FullName, "nested", "vault.dat");
        var time = new ManualTime();
        var vault = new ProtectedFileVault(path, new XorProtector(), time);
        vault.Save("Alice", "a-pass");
        time.Advance();
        vault.Save("Bob", "b-pass");
        time.Advance();
        vault.Touch("alice");

        Assert.DoesNotContain("a-pass", File.ReadAllText(path), StringComparison.Ordinal);
        var reloaded = new ProtectedFileVault(path, new XorProtector());
        Assert.Equal(["Alice", "Bob"], reloaded.Entries.Select(e => e.Name));

        reloaded.Save("BOB", "new");
        reloaded.Remove("alice");
        Assert.Equal([("BOB", "new")], reloaded.Entries.Select(e => (e.Name, e.Password)));
    }

    [Fact]
    public void Unreadable_vault_starts_empty_and_is_kept_aside()
    {
        var path = Path.Combine(_dir.FullName, "vault.dat");
        File.WriteAllText(path, "not encrypted json at all");
        var vault = new ProtectedFileVault(path, new XorProtector());
        Assert.Empty(vault.Entries);
        Assert.True(File.Exists(path + ".unreadable"));
    }

    [Fact]
    public async Task Reveal_tries_saved_passwords_and_offers_to_remember_new_ones()
    {
        var vault = new ProtectedFileVault(Path.Combine(_dir.FullName, "vault.dat"), new XorProtector());
        vault.Save("wrong one", "nope");
        var vm = Reveal(vault);
        await vm.LoadImageFromBytesAsync("s.png", StegoPng("secret", "friend-pass"));

        Assert.True(vm.TryVaultCommand.CanExecute(null));
        await vm.TryVaultCommand.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);

        vm.Password = "friend-pass";
        await vm.RevealCommand.ExecuteAsync(null);
        Assert.True(vm.CanRemember);
        vm.RememberName = "Mint";
        vm.RememberPasswordCommand.Execute(null);
        Assert.Contains(vault.Entries, e => e is { Name: "Mint", Password: "friend-pass" });

        await vm.LoadImageFromBytesAsync("s.png", StegoPng("secret", "friend-pass"));
        await vm.TryVaultCommand.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        Assert.Contains("Mint", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("secret", vm.Items[0].Text);
    }

    [Fact]
    public async Task Revealed_files_get_safe_previews_and_copied_text_is_cleared()
    {
        var vm = Reveal();
        await vm.LoadImageFromBytesAsync("s.png", StegoPng(
            entries:
            [
                PayloadEntry.FromText("msg"),
                PayloadEntry.FromFile("notes.md", "# สวัสดี\nline two"u8),
                PayloadEntry.FromFile("photo.PNG", PngBytes(8, 8)),
                PayloadEntry.FromFile("binary.txt", [0xFF, 0xFE, 0x00, 0x81]),
            ]));
        vm.Password = Password;
        await vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal("# สวัสดี\nline two", vm.Items[1].PreviewText);
        Assert.True(vm.Items[2].IsImagePreview);
        Assert.False(vm.Items[3].HasTextPreview);

        vm.CopyTextCommand.Execute(vm.Items[0]);
        Assert.Equal("msg", _clipboard.Text);
        await vm.ClipboardClearTask;
        Assert.Null(_clipboard.Text);
    }

    [Fact]
    public async Task Sample_image_reveals_with_the_sample_password()
    {
        var vm = Reveal();
        await vm.LoadSampleAsync(Codec);
        Assert.Contains(SampleFactory.Password, vm.StatusMessage, StringComparison.Ordinal);

        vm.Password = SampleFactory.Password;
        await vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(Strings.SampleMessage, vm.Items[0].Text);
    }

    [Fact]
    public async Task Watched_folder_reveals_new_images_with_saved_passwords()
    {
        var vault = new ProtectedFileVault(Path.Combine(_dir.FullName, "vault.dat"), new XorProtector());
        vault.Save("Ploy", "ploy-pass");
        var revealer = new AutoRevealer(Engine(), Loader(), vault);
        var inbox = _dir.CreateSubdirectory("Downloads");
        var found = new TaskCompletionSource<AutoRevealResult?>();
        using var watcher = new FolderWatcher(
            inbox.FullName,
            async path => found.TrySetResult(await revealer.TryFileAsync(path)),
            TimeSpan.FromMilliseconds(100));

        // Browsers write to a temporary name first and then rename.
        var temp = Path.Combine(inbox.FullName, "download.tmp");
        File.WriteAllBytes(temp, StegoPng("from the folder", "ploy-pass"));
        File.Move(temp, Path.Combine(inbox.FullName, "photo.png"));

        var result = await found.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("Ploy", result.VaultName);
        Assert.Equal("from the folder", result.Payload.Entries[0].GetText());

        var vm = Reveal(vault);
        vm.ShowAutoRevealed(result);
        Assert.True(vm.HasImage);
        Assert.Contains("Ploy", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_manage_vault_watch_folder_explorer_and_welcome()
    {
        var store = new MemorySettingsStore();
        var vault = new ProtectedFileVault(Path.Combine(_dir.FullName, "vault.dat"), new XorProtector());
        vault.Save("A", "1");
        var explorer = new FakeExplorer();
        _dialogs.FolderPath = _dir.FullName;
        var vm = new SettingsViewModel(store, new FakeTheme(), new AppSettings(), vault: vault, explorer: explorer, dialogs: _dialogs);

        Assert.True(vm.ShowWelcome);
        vm.MarkWelcomeSeen();
        Assert.False(store.Current.ShowWelcome);

        Assert.Single(vm.VaultEntries);
        vm.RemoveVaultEntryCommand.Execute(vm.VaultEntries[0]);
        Assert.False(vm.HasVaultEntries);
        Assert.Empty(vault.Entries);

        var changes = 0;
        vm.WatchFolderChanged += (_, _) => changes++;
        await vm.ChooseWatchFolderCommand.ExecuteAsync(null);
        Assert.Equal(_dir.FullName, store.Current.WatchFolder);
        Assert.True(vm.IsWatching);
        vm.StopWatchingCommand.Execute(null);
        Assert.Null(store.Current.WatchFolder);
        Assert.Equal(2, changes);

        vm.ExplorerMenuEnabled = true;
        Assert.True(explorer.IsEnabled);
    }

    private HideViewModel Hide(IPasswordVault? vault = null) =>
        new(Engine(), Codec, Loader(), _dialogs, _clipboard, _shell, TimeSpan.Zero, ClearDelay, vault);

    private RevealViewModel Reveal(IPasswordVault? vault = null) =>
        new(Engine(), Loader(), _dialogs, _clipboard, _shell, vault is null ? null : new AutoRevealer(Engine(), Loader(), vault), ClearDelay);

    private static byte[] StegoPng(string text = "x", string password = Password, PayloadEntry[]? entries = null)
    {
        using var input = new MemoryStream(PngBytes(160, 120));
        var stego = Engine().Embed(Codec.Decode(input, false), new Payload(entries ?? [PayloadEntry.FromText(text)]), password);
        using var output = new MemoryStream();
        Codec.EncodePng(stego, output);
        return output.ToArray();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance() => _now = _now.AddMinutes(1);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
