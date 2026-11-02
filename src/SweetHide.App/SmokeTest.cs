using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SweetHide.App;

/// <summary>
/// <c>SweetHide.exe --smoke-test &lt;dir&gt; [--lang th|en] [--theme light|dark]</c>: CI launches the real app, drives
/// every page through its view models (hide setup, reveal, analyze, settings), saves a screenshot of each into
/// &lt;dir&gt; and exits with 0. Any failure is written to &lt;dir&gt;/error.txt and the exit code is 1.
/// </summary>
internal static class SmokeTest
{
    private const string Password = "smoke-test passphrase 2026";

    public static string? OutputDirectory { get; private set; }

    public static string? Language { get; private set; }

    public static string? Theme { get; private set; }

    public static bool IsEnabled => OutputDirectory is not null;

    public static void ParseArguments(string[] args)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--smoke-test":
                    OutputDirectory = Path.GetFullPath(args[i + 1]);
                    break;
                case "--lang":
                    Language = args[i + 1];
                    break;
                case "--theme":
                    Theme = args[i + 1];
                    break;
            }
        }
    }

    public static void Fail(Exception error)
    {
        if (OutputDirectory is not null)
        {
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(Path.Combine(OutputDirectory, "error.txt"), error.ToString());
        }

        Environment.Exit(1);
    }

    public static async Task RunAsync(MainWindow window)
    {
        try
        {
            var dir = Directory.CreateDirectory(OutputDirectory!).FullName;
            var services = App.Services;
            await Task.Delay(1500);

            // Hide page, filled in the way a user would.
            var cover = SampleImagePng();
            await services.Hide.LoadCoverFromBytesAsync("sunset.png", cover);
            services.Hide.MessageText = "พบกันที่ร้านกาแฟ 10 โมงนะ ☕\nSee you at the café at 10.";
            var attachment = Path.Combine(dir, "plan.txt");
            await File.WriteAllTextAsync(attachment, "SweetHide smoke test attachment");
            await services.Hide.AddFilesFromPathsAsync([attachment]);
            await services.Hide.MeasureTask;
            services.Hide.Password = Password;
            services.Hide.ConfirmPassword = Password;
            Require(services.Hide.HideCommand.CanExecute(null), "Hide should be ready to run");
            await CaptureAsync(window, dir, "1-hide.png");

            // Reveal page: reveal a stego image made with the same engine.
            var stego = MakeStego(cover, services.Hide.MessageText, attachment);
            window.NavigateTo("reveal");
            await services.Reveal.LoadImageFromBytesAsync("sunset-secret.png", stego);
            services.Reveal.Password = Password;
            await services.Reveal.RevealCommand.ExecuteAsync(null);
            Require(services.Reveal.Items.Count == 2, $"Reveal found {services.Reveal.Items.Count} items, expected 2");
            await CaptureAsync(window, dir, "2-reveal.png");

            // Analyze page.
            window.NavigateTo("analyze");
            await services.Analyze.LoadImageFromBytesAsync("sunset-secret.png", stego);
            Require(services.Analyze.Rows.Count == 3, "Analyze should list three channels");
            await CaptureAsync(window, dir, "3-analyze.png");

            // Settings page.
            window.NavigateTo("settings");
            await CaptureAsync(window, dir, "4-settings.png");

            Application.Current.Exit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static byte[] MakeStego(byte[] coverPng, string text, string attachmentPath)
    {
        var codec = new SkiaImageCodec();
        using var input = new MemoryStream(coverPng);
        var payload = new Payload([PayloadEntry.FromText(text), PayloadEntry.FromFile(Path.GetFileName(attachmentPath), File.ReadAllBytes(attachmentPath))]);
        var stego = new StegoEngine().Embed(codec.Decode(input, applyOrientation: true), payload, Password);
        using var output = new MemoryStream();
        codec.EncodePng(stego, output);
        return output.ToArray();
    }

    /// <summary>A 960×640 sunset-like gradient with a little noise, so it behaves like a photo.</summary>
    private static byte[] SampleImagePng()
    {
        const int width = 960;
        const int height = 640;
        var random = new Random(2026);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var t = (double)y / height;
            for (var x = 0; x < width; x++)
            {
                var sun = Math.Max(0, 1 - (Math.Sqrt(Math.Pow(x - (width * 0.7), 2) + Math.Pow(y - (height * 0.55), 2)) / 140));
                var i = ((y * width) + x) * 4;
                data[i] = Clamp((255 * (1 - (t * 0.3))) + (sun * 40) + random.Next(-4, 5));
                data[i + 1] = Clamp((120 + (t * 60)) + (sun * 110) + random.Next(-4, 5));
                data[i + 2] = Clamp((170 - (t * 60)) + (sun * 20) + random.Next(-4, 5));
                data[i + 3] = 255;
            }
        }

        using var stream = new MemoryStream();
        new SkiaImageCodec().EncodePng(new PixelBuffer(width, height, data), stream);
        return stream.ToArray();
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)value, 0, 255);

    /// <summary>Saves the window as seen, plus "-full" with the whole scrollable page.</summary>
    private static async Task CaptureAsync(MainWindow window, string dir, string name)
    {
        await Task.Delay(1200); // let layout, bindings and image decoding settle
        await RenderToFileAsync(window.Content, Path.Combine(dir, name));

        if (window.CurrentPage?.Content is ScrollViewer { Content: Panel page })
        {
            var background = page.Background;
            page.Background = window.Content is Panel root ? root.Background : null;
            await RenderToFileAsync(page, Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "-full.png"));
            page.Background = background;
        }
    }

    private static async Task RenderToFileAsync(UIElement element, string path)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);
        var pixels = (await target.GetPixelsAsync()).ToArray();

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)target.PixelWidth,
            (uint)target.PixelHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        await using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }
}
