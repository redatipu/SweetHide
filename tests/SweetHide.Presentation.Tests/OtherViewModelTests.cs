using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

using static SweetHide.Presentation.Tests.TestEnvironment;

namespace SweetHide.Presentation.Tests;

public class AnalyzeViewModelTests
{
    [Fact]
    public async Task Analyzes_channels_and_renders_bit_planes()
    {
        var clipboard = new FakeClipboard { Image = new("img.png", PngBytes(64, 32)) };
        var vm = new AnalyzeViewModel(Codec, Loader(), new FakeDialogs(), clipboard, new FakeShell());

        await vm.PasteImageCommand.ExecuteAsync(null);

        Assert.Equal(["R", "G", "B"], vm.Rows.Select(r => r.Channel));
        Assert.NotNull(vm.BitPlanePng);
        var first = vm.BitPlanePng;
        using (var stream = new MemoryStream(first))
        {
            var plane = Codec.Decode(stream, false);
            Assert.Equal(64, plane.Width);
        }

        vm.BitPlane = 7;
        await vm.WorkTask;
        Assert.NotEqual(first, vm.BitPlanePng);
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public void Saves_changes_applies_theme_and_asks_for_restart_on_language_change()
    {
        var store = new MemorySettingsStore();
        var theme = new FakeTheme();
        var vm = new SettingsViewModel(store, theme, new AppSettings());

        vm.ThemeIndex = (int)AppTheme.Dark;
        Assert.Equal([AppTheme.Dark], theme.Applied);
        Assert.False(vm.ShowRestartHint);

        vm.LanguageIndex = (int)AppLanguage.Thai;
        Assert.True(vm.ShowRestartHint);
        Assert.Equal(new AppSettings(AppLanguage.Thai, AppTheme.Dark), store.Current);

        vm.LanguageIndex = 99; // ignored
        Assert.Equal(AppLanguage.Thai, store.Current.Language);
        Assert.Equal(2, store.Saves);
        Assert.Matches(@"\d+\.\d+\.\d+", SettingsViewModel.Version);
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-settings-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Roundtrips_and_falls_back_to_defaults()
    {
        var path = Path.Combine(_dir.FullName, "nested", "settings.json");
        var store = new JsonSettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());

        store.Save(new AppSettings(AppLanguage.English, AppTheme.Light));
        Assert.Equal(new AppSettings(AppLanguage.English, AppTheme.Light), new JsonSettingsStore(path).Load());
        Assert.Contains("\"English\"", File.ReadAllText(path), StringComparison.Ordinal);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), store.Load());

        File.WriteAllText(path, """{ "Language": 42, "Theme": "Dark" }""");
        Assert.Equal(new AppSettings(AppLanguage.Default, AppTheme.Dark), store.Load());
    }
}

public class PasswordStrengthTests
{
    [Theory]
    [InlineData("", PasswordStrength.None)]
    [InlineData("abc", PasswordStrength.Weak)]
    [InlineData("password123!", PasswordStrength.Weak)]
    [InlineData("aaaaaaaaaaaaaaaaaaaa", PasswordStrength.Weak)]
    [InlineData("abcdefghij", PasswordStrength.Weak)]
    [InlineData("Abcdefgh1j", PasswordStrength.Fair)]
    [InlineData("correcthorse12", PasswordStrength.Good)]
    [InlineData("correct horse battery", PasswordStrength.Strong)]
    [InlineData("แมวน้ำกินปลาทูทุกวัน", PasswordStrength.Fair)]       // 15 visible characters (marks don't count)
    [InlineData("แมวน้ำกินปลาทูทุกวันตอนเช้า", PasswordStrength.Good)]
    [InlineData("แมวน้ำกินปลาทู 2 ตัว ทุกวัน", PasswordStrength.Strong)]
    public void Evaluates(string password, PasswordStrength expected) =>
        Assert.Equal(expected, PasswordStrengthMeter.Evaluate(password));
}

public class ImageLoaderTests
{
    [Fact]
    public async Task Fully_transparent_clipboard_bitmaps_become_opaque()
    {
        var image = await Loader().LoadBytesAsync("clip.png", PngBytes(30, 30, alpha: 0), ImagePurpose.Cover);
        Assert.True(image.Pixels.IsOpaque);
        Assert.True(image.CapacityFor(1) > 0);
    }

    [Fact]
    public void Partially_transparent_images_are_left_alone()
    {
        var data = new byte[2 * 4];
        data[3] = 0;
        data[7] = 128;
        var pixels = new PixelBuffer(2, 1, data);
        ImageLoader.MakeOpaqueIfFullyTransparent(pixels);
        Assert.Equal(0, pixels.Pixels[3]);
        Assert.Equal(128, pixels.Pixels[7]);
    }
}
