using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.Tests;

internal sealed class FakeDialogs : IFileDialogService
{
    public string? ImagePath { get; set; }

    public List<string> Files { get; } = [];

    public string? SavePath { get; set; }

    public string? FolderPath { get; set; }

    public string? LastSuggestedName { get; private set; }

    /// <summary>What <see cref="PickImagesAsync"/> returns.</summary>
    public List<string> Images { get; } = [];

    /// <summary>What <see cref="PickFileAsync"/> returns.</summary>
    public string? FilePath { get; set; }

    public IReadOnlyList<string>? LastFileTypes { get; private set; }

    public Task<string?> PickImageAsync() => Task.FromResult(ImagePath);

    public Task<IReadOnlyList<string>> PickImagesAsync() => Task.FromResult<IReadOnlyList<string>>([.. Images]);

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(Files);

    public Task<string?> PickFileAsync(IReadOnlyList<string> extensions)
    {
        LastFileTypes = extensions;
        return Task.FromResult(FilePath);
    }

    public Task<string?> PickSavePngAsync(string suggestedFileName)
    {
        LastSuggestedName = suggestedFileName;
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickSaveFileAsync(string suggestedFileName)
    {
        LastSuggestedName = suggestedFileName;
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickFolderAsync() => Task.FromResult(FolderPath);
}

internal sealed class FakeClipboard : IClipboardService
{
    public ImageInput? Image { get; set; }

    public string? Text { get; private set; }

    public List<string> Files { get; } = [];

    public Task<ImageInput?> GetImageAsync() => Task.FromResult(Image);

    public void SetText(string text) => Text = text;

    public Task CopyFilesAsync(IReadOnlyList<string> paths)
    {
        Files.AddRange(paths);
        return Task.CompletedTask;
    }

    public void ClearIfText(string text)
    {
        if (Text == text)
        {
            Text = null;
        }
    }
}

internal sealed class FakeExplorer : IExplorerIntegration
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

/// <summary>Stands in for DPAPI: reversible, but the file is not plain JSON.</summary>
internal sealed class XorProtector : IDataProtector
{
    public byte[] Protect(byte[] data) => [.. data.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] data) => Protect(data);
}

internal sealed class FakeShell : IShellService
{
    public List<string> Shown { get; } = [];

    public List<Uri> Opened { get; } = [];

    public void ShowInFolder(string path) => Shown.Add(path);

    public void OpenUri(Uri uri) => Opened.Add(uri);
}

internal sealed class FakeTheme : IThemeService
{
    public List<AppTheme> Applied { get; } = [];

    public void Apply(AppTheme theme) => Applied.Add(theme);
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public int Saves { get; private set; }

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        Saves++;
    }
}

internal static class TestEnvironment
{
    public const string Password = "ร้านกาแฟ-hidden-2026";

    public static readonly KdfParameters FastKdf = new(64, 1, 1);

    public static readonly SkiaImageCodec Codec = new();

    public static StegoEngine Engine() => new(FastKdf);

    public static ImageLoader Loader() => new(Codec);

    /// <summary>A noisy RGB(A) image encoded as PNG.</summary>
    public static byte[] PngBytes(int width, int height, int seed = 1, byte alpha = 255)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        random.NextBytes(data);
        for (var i = 3; i < data.Length; i += 4)
        {
            data[i] = alpha;
        }

        using var stream = new MemoryStream();
        Codec.EncodePng(new PixelBuffer(width, height, data), stream);
        return stream.ToArray();
    }

    public static byte[] RandomBytes(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
