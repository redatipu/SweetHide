using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

public sealed record ChannelRow(string Channel, string OnesRatio, string ChiSquare, string Probability, bool LooksEmbedded);

/// <summary>The "Analyze" page: LSB statistics, chi-square attack and a bit-plane view.</summary>
public sealed partial class AnalyzeViewModel : PageViewModel
{
    private readonly IImageCodec _codec;
    private readonly ImageLoader _loader;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private int _renderVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageInfo))]
    private LoadedImage? _image;

    [ObservableProperty]
    private int _bitPlane;

    [ObservableProperty]
    private byte[]? _bitPlanePng;

    public AnalyzeViewModel(IImageCodec codec, ImageLoader loader, IFileDialogService dialogs, IClipboardService clipboard, IShellService shell)
        : base(shell)
    {
        _codec = codec;
        _loader = loader;
        _dialogs = dialogs;
        _clipboard = clipboard;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    public ObservableCollection<ChannelRow> Rows { get; } = [];

    public IReadOnlyList<int> BitPlanes { get; } = [0, 1, 2, 3, 4, 5, 6, 7];

    public bool HasImage => Image is not null;

    public bool HasRows => Rows.Count > 0;

    public string ImageInfo => Image is null ? string.Empty : Strings.Format(Strings.ImageInfoFormat, Image.Width, Image.Height, Image.Name);

    /// <summary>The latest analysis/render; awaited by tests.</summary>
    public Task WorkTask { get; private set; } = Task.CompletedTask;

    public Task LoadImageFromPathAsync(string path) => LoadAsync(() => _loader.LoadFileAsync(path, ImagePurpose.Inspect));

    public Task LoadImageFromBytesAsync(string name, byte[] bytes) => LoadAsync(() => _loader.LoadBytesAsync(name, bytes, ImagePurpose.Inspect));

    private static string Number(double value, string format) =>
        double.IsNaN(value) ? "–" : value.ToString(format, CultureInfo.InvariantCulture);

    partial void OnBitPlaneChanged(int value)
    {
        if (Image is not null)
        {
            WorkTask = RenderAsync(Image.Pixels, value);
        }
    }

    private async Task LoadAsync(Func<Task<LoadedImage>> load)
    {
        IsBusy = true;
        ClearStatus();
        Rows.Clear();
        BitPlanePng = null;
        try
        {
            var image = await load();
            Image = image;
            var report = await Task.Run(() => LsbAnalyzer.Analyze(image.Pixels));
            foreach (var channel in report.Channels)
            {
                Rows.Add(new ChannelRow(
                    channel.Channel,
                    Number(channel.OnesRatio, "0.0000"),
                    Number(channel.ChiSquare, "0.0"),
                    Number(channel.EmbeddingProbability, "0.0000"),
                    channel.EmbeddingProbability > 0.95));
            }

            await RenderAsync(image.Pixels, BitPlane);
        }
        catch (Exception ex) when (ex is InvalidImageException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.Format(Strings.ImageLoadFailed, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RenderAsync(PixelBuffer pixels, int bit)
    {
        var version = Interlocked.Increment(ref _renderVersion);
        var png = await Task.Run(() =>
        {
            using var stream = new MemoryStream();
            _codec.EncodePng(LsbAnalyzer.RenderBitPlane(pixels, bit), stream);
            return stream.ToArray();
        });

        if (version == Volatile.Read(ref _renderVersion))
        {
            BitPlanePng = png; // ignore results overtaken by a newer bit choice
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        if (await _dialogs.PickImageAsync() is { } path)
        {
            WorkTask = LoadImageFromPathAsync(path);
            await WorkTask;
        }
    }

    [RelayCommand]
    private async Task LoadDroppedImageAsync(ImageInput? input)
    {
        if (input is not null)
        {
            WorkTask = LoadImageFromBytesAsync(input.Name, input.Bytes);
            await WorkTask;
        }
    }

    [RelayCommand]
    private async Task PasteImageAsync()
    {
        var image = await _clipboard.GetImageAsync();
        if (image is null)
        {
            ShowStatus(StatusSeverity.Warning, Strings.PasteImage, Strings.ClipboardNoImage);
            return;
        }

        WorkTask = LoadImageFromBytesAsync(image.Name, image.Bytes);
        await WorkTask;
    }
}
