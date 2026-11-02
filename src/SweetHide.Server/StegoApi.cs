using System.Buffers;
using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using SweetHide.Core;
using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Server;

internal sealed record RevealedFile(string Name, long Size, ReadOnlyMemory<byte> Data);

internal sealed record RevealResponse(IReadOnlyList<string> Messages, IReadOnlyList<RevealedFile> Files);

internal sealed record CapacityLevel(int BitsPerChannel, long ContainerBytes, long PayloadBytes);

internal sealed record CapacityResponse(int Width, int Height, long UsablePixels, int OverheadBytes, IReadOnlyList<CapacityLevel> Levels);

internal sealed record ChannelStats(string Channel, double OnesRatio, double? ChiSquare, int DegreesOfFreedom, double? EmbeddingProbability);

internal sealed record BitPlaneImage(int Bit, ReadOnlyMemory<byte> Png);

internal sealed record AnalyzeResponse(int Width, int Height, IReadOnlyList<ChannelStats> Channels, BitPlaneImage? BitPlane);

/// <summary>
/// POST /api/hide, /api/reveal, /api/capacity and /api/analyze. All take multipart/form-data, keep everything in
/// memory and run the image work through <see cref="JobLimiter"/>.
/// </summary>
internal static class StegoApi
{
    public const string MessagesFileName = "messages.txt";

    public static void MapStegoApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(ServerApp.ApiPrefix).RequireRateLimiting(RateLimiting.ApiPolicy);
        api.MapPost("/hide", HideAsync);
        api.MapPost("/reveal", RevealAsync);
        api.MapPost("/capacity", CapacityAsync);
        api.MapPost("/analyze", AnalyzeAsync);
    }

    /// <summary>
    /// Fields: <c>cover</c> (image), <c>text</c> (repeatable), <c>file</c> (repeatable), <c>password</c>,
    /// <c>bits</c> (1-4, default 1). Returns the stego image as a PNG download named after the cover.
    /// </summary>
    private static async Task<IResult> HideAsync(HttpContext context, StegoEngine engine, IImageCodec codec, JobLimiter jobs, IOptions<ServerOptions> options)
    {
        var form = await ReadFormAsync(context, options.Value);
        var cover = RequiredFile(form, "cover");
        var password = RequiredPassword(form);
        var bits = OptionalInt(form, "bits", 1, StegoEngine.SupportedBitsPerChannel[^1]) ?? StegoEngine.DefaultBitsPerChannel;
        var payload = await BuildPayloadAsync(form, context.RequestAborted);

        var png = await jobs.RunAsync(
            () =>
            {
                var image = Decode(codec, cover, applyOrientation: true);
                PixelBuffer stego;
                try
                {
                    stego = engine.Embed(image, payload, password, bits);
                }
                catch (StegoException ex) when (ex.Error == StegoError.InsufficientCapacity)
                {
                    throw CapacityProblem(image, payload, bits);
                }
                catch (ArgumentException ex)
                {
                    // E.g. more than the format's 256 MiB, only reachable when MaxRequestBytes is raised that high.
                    throw new ApiProblemException(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "These items can't be hidden.", ex.Message);
                }

                return EncodePng(codec, stego);
            },
            context.RequestAborted);

        return Results.Stream(png, "image/png", DownloadName(cover.FileName, ".png"));
    }

    /// <summary>
    /// Fields: <c>image</c>, <c>password</c>. Returns JSON (messages, and files as base64 with sanitized names), or with
    /// <c>?format=zip</c> a zip of the files plus messages.txt.
    /// </summary>
    private static async Task<IResult> RevealAsync(HttpContext context, StegoEngine engine, IImageCodec codec, JobLimiter jobs, IOptions<ServerOptions> options, string? format)
    {
        var zip = format?.ToUpperInvariant() switch
        {
            null or "" or "JSON" => false,
            "ZIP" => true,
            _ => throw new ApiProblemException(
                StatusCodes.Status400BadRequest, ProblemCodes.InvalidField, "Unknown format.", "Use format=json (default) or format=zip."),
        };

        var form = await ReadFormAsync(context, options.Value);
        var file = RequiredFile(form, "image");
        var password = RequiredPassword(form);

        // Stego images are read exactly as stored: no EXIF rotation.
        var payload = await jobs.RunAsync(() => engine.Extract(Decode(codec, file, applyOrientation: false), password), context.RequestAborted);

        if (zip)
        {
            return Results.Stream(BuildZip(payload), "application/zip", DownloadName(file.FileName, ".zip"));
        }

        var messages = payload.Entries.Where(e => e.Kind == PayloadEntryKind.Text).Select(e => e.GetText()).ToList();
        var files = payload.Entries
            .Where(e => e.Kind == PayloadEntryKind.File)
            .Select(e => new RevealedFile(SafeFileName.Sanitize(e.Name), e.Length, e.Content))
            .ToList();
        return Results.Ok(new RevealResponse(messages, files));
    }

    /// <summary>Field: <c>image</c>. Returns the size and how much it can hold at each bit depth.</summary>
    private static async Task<IResult> CapacityAsync(HttpContext context, IImageCodec codec, JobLimiter jobs, IOptions<ServerOptions> options)
    {
        var form = await ReadFormAsync(context, options.Value);
        var file = RequiredFile(form, "image");

        var response = await jobs.RunAsync(
            () =>
            {
                // Same orientation as /api/hide uses for covers.
                var image = Decode(codec, file, applyOrientation: true);
                var levels = StegoEngine.SupportedBitsPerChannel
                    .Select(bits => StegoEngine.GetCapacity(image, bits))
                    .Select(c => new CapacityLevel(c.BitsPerChannel, c.ContainerBytes, c.PayloadBytes))
                    .ToList();
                var first = StegoEngine.GetCapacity(image, StegoEngine.DefaultBitsPerChannel);
                return new CapacityResponse(image.Width, image.Height, first.UsablePixels, StegoEngine.ContainerOverhead, levels);
            },
            context.RequestAborted);
        return Results.Ok(response);
    }

    /// <summary>Fields: <c>image</c>, optional <c>bitPlane</c> (0-7) to also get that bit plane as a PNG.</summary>
    private static async Task<IResult> AnalyzeAsync(HttpContext context, IImageCodec codec, JobLimiter jobs, IOptions<ServerOptions> options)
    {
        var form = await ReadFormAsync(context, options.Value);
        var file = RequiredFile(form, "image");
        var bitPlane = OptionalInt(form, "bitPlane", 0, 7);

        var response = await jobs.RunAsync(
            () =>
            {
                var image = Decode(codec, file, applyOrientation: false);
                var report = LsbAnalyzer.Analyze(image);
                var channels = report.Channels
                    .Select(c => new ChannelStats(c.Channel, c.OnesRatio, Finite(c.ChiSquare), c.DegreesOfFreedom, Finite(c.EmbeddingProbability)))
                    .ToList();
                BitPlaneImage? plane = null;
                if (bitPlane is { } bit)
                {
                    using var png = EncodePng(codec, LsbAnalyzer.RenderBitPlane(image, bit));
                    plane = new BitPlaneImage(bit, png.ToArray());
                }

                return new AnalyzeResponse(report.Width, report.Height, channels, plane);
            },
            context.RequestAborted);
        return Results.Ok(response);
    }

    /// <summary>
    /// Zip of the revealed files (sanitized, de-duplicated names) plus messages.txt when there are messages.
    /// </summary>
    internal static MemoryStream BuildZip(Payload payload)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var messages = payload.Entries.Where(e => e.Kind == PayloadEntryKind.Text).Select(e => e.GetText()).ToList();
            if (messages.Count > 0)
            {
                Add(archive, UniqueName(MessagesFileName, used), Encoding.UTF8.GetBytes(MessagesText(messages)));
            }

            foreach (var entry in payload.Entries.Where(e => e.Kind == PayloadEntryKind.File))
            {
                Add(archive, UniqueName(SafeFileName.Sanitize(entry.Name), used), entry.Content.Span);
            }
        }

        stream.Position = 0;
        return stream;

        static void Add(ZipArchive archive, string name, ReadOnlySpan<byte> content)
        {
            using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            output.Write(content);
        }
    }

    internal static string MessagesText(IReadOnlyList<string> messages) => messages.Count == 1
        ? messages[0]
        : string.Join("\n\n", messages.Select((message, i) => $"===== {i + 1} =====\n{message}"));

    /// <summary>"name.ext", then "name (1).ext", "name (2).ext", ... like files revealed by the other apps.</summary>
    internal static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The upload's name with a new extension, e.g. "beach.jpg" → "beach.png". Never reveals what's inside.</summary>
    internal static string DownloadName(string? uploadedName, string extension)
    {
        var stem = string.IsNullOrWhiteSpace(uploadedName) ? string.Empty : Path.GetFileNameWithoutExtension(SafeFileName.Sanitize(uploadedName));
        return (string.IsNullOrWhiteSpace(stem) ? "image" : stem) + extension;
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext context, ServerOptions options)
    {
        var request = context.Request;

        // Kestrel enforces the same limit while reading; checking the declared length first answers before uploading.
        if (request.ContentLength > options.MaxRequestBytes)
        {
            throw ApiProblemException.RequestTooLarge(options.MaxRequestBytes);
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiProblemException(
                StatusCodes.Status415UnsupportedMediaType, ProblemCodes.UnsupportedMediaType, "Send the fields as multipart/form-data.");
        }

        return await request.ReadFormAsync(context.RequestAborted);
    }

    private static IFormFile RequiredFile(IFormCollection form, string name)
    {
        var files = form.Files.GetFiles(name);
        if (files.Count == 0 || files[0].Length == 0)
        {
            throw ApiProblemException.MissingField(name);
        }

        return files.Count == 1 ? files[0] : throw ApiProblemException.InvalidField(name, "Send exactly one image.");
    }

    /// <summary>Taken exactly as typed (no trimming): spaces can be part of a passphrase.</summary>
    private static string RequiredPassword(IFormCollection form)
    {
        var values = form["password"];
        if (values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            throw ApiProblemException.MissingField("password");
        }

        return values.Count == 1 ? values[0]! : throw ApiProblemException.InvalidField("password", "Send one password.");
    }

    private static int? OptionalInt(IFormCollection form, string name, int min, int max)
    {
        var values = form[name];
        if (values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return null;
        }

        return values.Count == 1
            && int.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max
            ? value
            : throw ApiProblemException.InvalidField(name, $"Use a whole number from {min} to {max}.");
    }

    private static async Task<Payload> BuildPayloadAsync(IFormCollection form, CancellationToken cancellationToken)
    {
        var entries = new List<PayloadEntry>();
        foreach (var text in form["text"])
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            try
            {
                entries.Add(PayloadEntry.FromText(text));
            }
            catch (EncoderFallbackException)
            {
                throw new ApiProblemException(StatusCodes.Status400BadRequest, ProblemCodes.InvalidText, "The message contains characters that can't be saved.");
            }
        }

        foreach (var file in form.Files.GetFiles("file"))
        {
            // An empty part without a name is what a form sends for "no file chosen".
            if (file.Length == 0 && string.IsNullOrWhiteSpace(file.FileName))
            {
                continue;
            }

            entries.Add(await ReadFileEntryAsync(file, cancellationToken));
        }

        if (entries.Count == 0)
        {
            throw new ApiProblemException(
                StatusCodes.Status400BadRequest, ProblemCodes.NothingToHide, "Nothing to hide.", "Add a 'text' field and/or a 'file'.");
        }

        try
        {
            return new Payload(entries);
        }
        catch (ArgumentException ex)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "These items can't be hidden.", ex.Message);
        }
    }

    private static async Task<PayloadEntry> ReadFileEntryAsync(IFormFile file, CancellationToken cancellationToken)
    {
        // The entry keeps its own copy; the pooled read buffer is wiped before going back to the pool.
        var length = checked((int)file.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            await using (var stream = file.OpenReadStream())
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            }

            // Browsers send just the name; anything path-like from other clients is cut down here.
            return PayloadEntry.FromFile(SafeFileName.Sanitize(file.FileName), buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static PixelBuffer Decode(IImageCodec codec, IFormFile file, bool applyOrientation)
    {
        using var stream = file.OpenReadStream();
        return codec.Decode(stream, applyOrientation);
    }

    private static MemoryStream EncodePng(IImageCodec codec, PixelBuffer image)
    {
        var output = new MemoryStream();
        try
        {
            codec.EncodePng(image, output);
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static ApiProblemException CapacityProblem(PixelBuffer image, Payload payload, int bits)
    {
        var needed = StegoEngine.GetContainerSize(payload);
        var available = StegoEngine.GetCapacity(image, bits).ContainerBytes;
        var suggested = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > bits && StegoEngine.GetCapacity(image, b).ContainerBytes >= needed)
            .Select(b => (int?)b)
            .FirstOrDefault();
        return new ApiProblemException(
            StatusCodes.Status422UnprocessableEntity,
            ProblemCodes.InsufficientCapacity,
            "The data does not fit in this image.",
            $"Needs {ByteSize.Format(needed)} but this {image.Width}x{image.Height} image holds {ByteSize.Format(available)} at {bits} bit(s) per channel.",
            new Dictionary<string, object?>
            {
                ["neededBytes"] = needed,
                ["capacityBytes"] = available,
                ["bitsPerChannel"] = bits,
                ["suggestedBitsPerChannel"] = suggested,
            });
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
