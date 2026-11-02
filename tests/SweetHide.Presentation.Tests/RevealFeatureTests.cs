using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Robust;
using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public sealed class RevealFeatureTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-reveal2-");
    private readonly FakeDialogs _dialogs = new();
    private readonly ProtectedKeyring _keyring;
    private readonly RevealViewModel _vm;

    public RevealFeatureTests()
    {
        _keyring = new ProtectedKeyring(Path.Combine(_dir.FullName, "keys.dat"), new XorProtector());
        _vm = new RevealViewModel(Engine(), Loader(), _dialogs, new FakeClipboard(), new FakeShell(), keyring: _keyring);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Shows_how_the_data_was_hidden()
    {
        var cover = Cover(160, 120);
        var options = new EmbedOptions { BitsPerChannel = 2, KdfProfile = KdfProfile.Paranoid };
        await LoadAsync("s.png", Engine().Embed(cover, Payload.FromText("info"), Password, keyFile: null, options));

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal("info", _vm.Items[0].Text);
        Assert.True(_vm.HasDetails);
        Assert.Equal(
            [
                (Strings.DetailFormat, Strings.Format(Strings.DetailFormatValue, 2)),
                (Strings.DetailProtection, Strings.ModePassword),
                (Strings.DetailKdf, Strings.KdfParanoid),
                (Strings.DetailBits, "2"),
                (Strings.DetailAdaptive, Strings.No),
            ],
            _vm.Details.Select(d => (d.Label, d.Value)));
    }

    [Fact]
    public async Task Needs_the_key_file_the_image_was_hidden_with()
    {
        var keyPath = Path.Combine(_dir.FullName, "song.mp3");
        File.WriteAllBytes(keyPath, RandomBytes(5000));
        var stego = Engine().Embed(Cover(160, 120), Payload.FromText("two factors"), Password, KeyFile.FromFile(keyPath));
        await LoadAsync("s.png", stego);

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal(Strings.RevealNotFound, _vm.StatusMessage);

        _dialogs.FilePath = keyPath;
        await _vm.ChooseKeyFileCommand.ExecuteAsync(null);
        Assert.True(_vm.HasKeyFile);
        Assert.Equal("song.mp3", _vm.KeyFileText);
        Assert.Empty(_dialogs.LastFileTypes!); // any kind of file can be a key file

        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal("two factors", _vm.Items[0].Text);
        Assert.False(_vm.CanRemember); // a password that needs a key file never opens anything alone

        _vm.ClearKeyFileCommand.Execute(null);
        Assert.False(_vm.HasKeyFile);
        Assert.Equal(Strings.KeyFileNone, _vm.KeyFileText);
    }

    [Fact]
    public async Task An_empty_key_file_is_refused()
    {
        _dialogs.FilePath = Path.Combine(_dir.FullName, "empty.bin");
        File.WriteAllBytes(_dialogs.FilePath, []);

        await _vm.ChooseKeyFileCommand.ExecuteAsync(null);

        Assert.False(_vm.HasKeyFile);
        Assert.Equal(StatusSeverity.Error, _vm.StatusSeverity);
        Assert.Equal(Strings.KeyFileEmpty, _vm.StatusMessage);
    }

    [Fact]
    public async Task Opens_images_hidden_for_my_key()
    {
        Assert.False(_vm.HasMyKey);
        var publicKey = _keyring.GenerateIdentity();
        Assert.True(_vm.HasMyKey); // follows the keyring without reloading the page

        var stego = Engine().Embed(Cover(200, 150), Payload.FromText("for you"), [Recipient.Parse(publicKey)]);
        await LoadAsync("s.png", stego);
        Assert.True(_vm.RevealWithMyKeyCommand.CanExecute(null));
        Assert.False(_vm.RevealCommand.CanExecute(null)); // no password needed

        await _vm.RevealWithMyKeyCommand.ExecuteAsync(null);

        Assert.Equal("for you", _vm.Items[0].Text);
        Assert.Contains(_vm.Details, d => d.Label == Strings.DetailProtection && d.Value == Strings.ModeRecipients);
        Assert.DoesNotContain(_vm.Details, d => d.Label == Strings.DetailKdf);
    }

    [Fact]
    public async Task Says_so_when_an_image_was_not_hidden_for_my_key()
    {
        _keyring.GenerateIdentity();
        using var someoneElse = Identity.Generate();
        await LoadAsync("s.png", Engine().Embed(Cover(200, 150), Payload.FromText("x"), [someoneElse.Recipient]));

        await _vm.RevealWithMyKeyCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Warning, _vm.StatusSeverity);
        Assert.Equal(Strings.RevealNotForMyKey, _vm.StatusMessage);
        Assert.Empty(_vm.Items);
    }

    [Fact]
    public async Task Falls_back_to_robust_mode_for_chat_app_photos()
    {
        var cover = Cover(800, 600);
        var payload = Payload.FromText("ok");
        Assert.True(StegoEngine.GetRobustPayloadSize(payload) <= StegoEngine.GetRobustCapacity(800, 600, RobustProfile.Line));
        var robust = Engine().EmbedRobust(cover, payload, Password, RobustProfile.Line);
        using var jpeg = new MemoryStream();
        RobustStego.WriteJpeg(robust, jpeg, RobustProfile.Line);
        await _vm.LoadImageFromBytesAsync("from-line.jpg", jpeg.ToArray());

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Success, _vm.StatusSeverity);
        Assert.Equal("ok", _vm.Items[0].Text);
        Assert.Equal(Strings.Format(Strings.ModeRobust, "LINE"), _vm.Details[0].Value);
        Assert.Equal(Strings.DetailBitErrors, _vm.Details[1].Label);
        Assert.Contains(Strings.DamageNone, _vm.Details[1].Value, StringComparison.Ordinal);
        Assert.Equal(Strings.DetailRepaired, _vm.Details[2].Label);
    }

    [Fact]
    public async Task Collects_split_images_one_at_a_time_until_there_are_enough()
    {
        var paths = await SplitAsync(threshold: 2, count: 3, "split secret");
        await _vm.LoadImageFromPathAsync(paths[2]);
        _vm.Password = Password;

        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal(StatusSeverity.Informational, _vm.StatusSeverity);
        Assert.Equal(Strings.ShareFoundTitle, _vm.StatusTitle);
        Assert.Equal(Strings.Format(Strings.ShareProgress, 1, 2, 3), _vm.StatusMessage);
        Assert.Equal([Strings.Format(Strings.ShareItemFormat, 3, 3, "part3.png")], _vm.Shares.Select(s => s.Text));
        Assert.Equal(Password, _vm.Password); // kept for the next image
        Assert.Empty(_vm.Items);

        // The same image again doesn't count twice.
        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.Single(_vm.Shares);

        await _vm.LoadImageFromPathAsync(paths[0]);
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Success, _vm.StatusSeverity);
        Assert.Equal(Strings.Format(Strings.SharesCombined, 2, 3), _vm.StatusMessage);
        Assert.Equal("split secret", _vm.Items[0].Text);
        Assert.Contains(_vm.Details, d => d.Value == Strings.Format(Strings.ModeSplit, 2, 3));
        Assert.False(_vm.HasShares);
        Assert.Empty(_vm.Password);
    }

    [Fact]
    public async Task Reveals_several_dropped_images_together()
    {
        var paths = await SplitAsync(threshold: 3, count: 3, "all three");

        await _vm.LoadDroppedImagesCommand.ExecuteAsync(paths);
        Assert.Equal("part1.png", _vm.Image!.Name);
        Assert.Equal(["part2.png", "part3.png"], _vm.AdditionalImages.Select(i => i.Name));

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal("all three", _vm.Items[0].Text);
    }

    [Fact]
    public async Task Add_images_appends_and_a_single_image_starts_over()
    {
        var paths = await SplitAsync(threshold: 2, count: 3, "x");
        await _vm.LoadImageFromPathAsync(paths[0]);
        _dialogs.Images.AddRange(paths[1..]);

        await _vm.AddImagesCommand.ExecuteAsync(null);
        Assert.Equal("part1.png", _vm.Image!.Name);
        Assert.Equal(2, _vm.AdditionalImages.Count);
        Assert.True(_vm.HasAdditionalImages);

        _vm.RemoveAdditionalImageCommand.Execute(_vm.AdditionalImages[0]);
        Assert.Equal(["part3.png"], _vm.AdditionalImages.Select(i => i.Name));

        await _vm.LoadImageFromBytesAsync("other.png", PngBytes(40, 40));
        Assert.False(_vm.HasAdditionalImages);
    }

    [Fact]
    public async Task A_part_of_another_split_starts_a_new_collection()
    {
        var first = await SplitAsync(threshold: 2, count: 2, "one", prefix: "a");
        var second = await SplitAsync(threshold: 2, count: 2, "two", prefix: "b");
        _vm.Password = Password;
        await _vm.LoadImageFromPathAsync(first[0]);
        await _vm.RevealCommand.ExecuteAsync(null);

        await _vm.LoadImageFromPathAsync(second[1]);
        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal(["b2.png"], _vm.Shares.Select(s => s.ImageName));

        _vm.ClearSharesCommand.Execute(null);
        Assert.False(_vm.HasShares);
        Assert.False(_vm.StatusIsOpen);
    }

    [Fact]
    public async Task Offers_stego_finder_when_nothing_is_found()
    {
        var png = PngBytes(64, 48);
        await _vm.LoadImageFromBytesAsync("plain.png", png);
        Assert.False(_vm.ScanWithFinderCommand.CanExecute(null));
        ImageInput? requested = null;
        _vm.ScanRequested += (_, input) => requested = input;

        _vm.Password = "whatever";
        await _vm.RevealCommand.ExecuteAsync(null);
        Assert.True(_vm.CanScanWithFinder);

        _vm.ScanWithFinderCommand.Execute(null);
        Assert.Equal("plain.png", requested!.Name);
        Assert.Equal(png, requested.Bytes);

        await _vm.LoadImageFromBytesAsync("next.png", png);
        Assert.False(_vm.CanScanWithFinder);
    }

    [Fact]
    public async Task Reveals_palette_pngs_through_the_palette()
    {
        var palette = Palette(160, 120);
        var stego = Engine().Embed(palette, Payload.FromText("256 colours"), Password);
        using var file = new MemoryStream();
        IndexedPng.Write(stego, file);
        await _vm.LoadImageFromBytesAsync("icon.png", file.ToArray());
        Assert.True(_vm.Image!.IsPalette);

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal("256 colours", _vm.Items[0].Text);
    }

    [Fact]
    public async Task Full_colour_data_in_a_palette_png_is_still_found()
    {
        // A palette PNG holding data the normal way would be odd, but the normal path must still be tried.
        var palette = Palette(160, 120);
        var stego = Engine().Embed(palette.ToPixels(), Payload.FromText("rgba"), Password);
        using var png = new MemoryStream();
        Codec.EncodePng(stego, png);
        await _vm.LoadImageFromBytesAsync("rgba.png", png.ToArray());

        _vm.Password = Password;
        await _vm.RevealCommand.ExecuteAsync(null);

        Assert.Equal("rgba", _vm.Items[0].Text);
    }

    [Fact]
    public void Every_engine_error_has_its_own_explanation()
    {
        var messages = Enum.GetValues<StegoError>()
            .Select(error => StegoText.ForError(new StegoException(error, "engine text")))
            .ToList();

        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m.Message)));
        Assert.Equal(messages.Count, messages.Select(m => m.Message).Distinct().Count());
        Assert.Equal(Strings.RevealCorrupted, StegoText.ForError(new StegoException(StegoError.Corrupted, "x")).Message);
        Assert.Equal(Strings.RevealNewerVersion, StegoText.ForError(new StegoException(StegoError.UnsupportedFormat, "x")).Message);
        Assert.Equal(Strings.SharesDoNotMatch, StegoText.ForError(new StegoException(StegoError.SharesDoNotMatch, "x")).Message);
    }

    [Fact]
    public async Task Trying_saved_passwords_on_a_split_image_does_not_crash()
    {
        var vault = new ProtectedFileVault(Path.Combine(_dir.FullName, "vault.dat"), new XorProtector());
        vault.Save("Ploy", Password);
        var vm = new RevealViewModel(Engine(), Loader(), _dialogs, new FakeClipboard(), new FakeShell(), new AutoRevealer(Engine(), Loader(), vault));
        var paths = await SplitAsync(threshold: 2, count: 2, "x");
        await vm.LoadImageFromPathAsync(paths[0]);

        await vm.TryVaultCommand.ExecuteAsync(null);

        Assert.Equal(Strings.VaultNoMatch, vm.StatusMessage);
    }

    internal static PixelBuffer Cover(int width, int height, int seed = 1) =>
        Codec.Decode(new MemoryStream(PngBytes(width, height, seed)), applyOrientation: false);

    /// <summary>A 256-colour gradient with a little noise.</summary>
    internal static IndexedImage Palette(int width, int height)
    {
        var palette = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            palette[i] = 0xFF000000u | ((uint)i << 16) | ((uint)(255 - (i / 2)) << 8) | (uint)((i * 3) % 256 / 2);
        }

        var random = new Random(11);
        var indices = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                indices[(y * width) + x] = (byte)Math.Clamp(((x * 255 / (width - 1)) + (y * 40 / height) + random.Next(-6, 7)) % 256, 0, 255);
            }
        }

        return new IndexedImage(width, height, indices, palette);
    }

    private async Task LoadAsync(string name, PixelBuffer stego)
    {
        using var png = new MemoryStream();
        Codec.EncodePng(stego, png);
        await _vm.LoadImageFromBytesAsync(name, png.ToArray());
    }

    private async Task<string[]> SplitAsync(int threshold, int count, string text, string prefix = "part")
    {
        var covers = Enumerable.Range(0, count).Select(i => Cover(160, 120, seed: i + 10)).ToList();
        var images = Engine().EmbedShares(covers, Payload.FromText(text), threshold, Password);
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            paths[i] = Path.Combine(_dir.FullName, $"{prefix}{i + 1}.png");
            await using var file = File.Create(paths[i]);
            Codec.EncodePng(images[i], file);
        }

        return paths;
    }
}
