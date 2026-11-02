using System.IO.Compression;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Robust;

namespace SweetHide.Cli;

internal sealed record HideOptions(
    IReadOnlyList<string> Inputs,
    string? Output,
    string? OutputDirectory,
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> TextFiles,
    IReadOnlyList<string> Files,
    int Bits,
    bool Force,
    bool PasswordFromStdin,
    LosslessFormat Format = LosslessFormat.Png)
{
    public KdfProfile Kdf { get; init; } = KdfProfile.Normal;

    public string? KeyFile { get; init; }

    public bool Adaptive { get; init; }

    public bool TrueColor { get; init; }

    public bool KeepExif { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public IReadOnlyList<string> DecoyTexts { get; init; } = [];

    public IReadOnlyList<string> DecoyFiles { get; init; } = [];

    public bool DecoyRandom { get; init; }

    public int? Split { get; init; }

    public string? Robust { get; init; }
}

/// <summary>One decoded cover: its raw bytes (for EXIF), its pixels, and the palette image when palette mode applies.</summary>
internal sealed record Cover(string Input, byte[] Bytes, PixelBuffer Pixels, IndexedImage? Indexed)
{
    public int Width => Indexed?.Width ?? Pixels.Width;

    public int Height => Indexed?.Height ?? Pixels.Height;

    public bool IsPalette => Indexed is not null;
}

internal sealed class HideCommand(CliContext context)
{
    public int Execute(HideOptions options)
    {
        Validate(options);
        var covers = DecodeCovers(options);
        ValidatePalette(options, covers);

        if (options.Robust is not null)
        {
            return ExecuteRobust(options, covers);
        }

        if (options.Recipients.Count > 0)
        {
            return ExecuteRecipients(options, covers);
        }

        if (options.Split is { } threshold)
        {
            return ExecuteSplit(options, covers, threshold);
        }

        if (IsDeniable(options))
        {
            return ExecuteDeniable(options, covers);
        }

        return ExecutePassword(options, covers);
    }

    private int ExecutePassword(HideOptions options, IReadOnlyList<Cover> covers)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        foreach (var cover in covers)
        {
            RequireFits(cover, containerSize, embedOptions);
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        var reports = new List<HideReport>(covers.Count);
        var toStdout = IsStdout(options);
        var info = toStdout ? context.Error : context.Out;
        foreach (var cover in covers)
        {
            var output = OutputPathFor(options, cover.Input);
            HideReport report;
            if (cover.Indexed is { } indexed)
            {
                var stego = engine.Embed(indexed, payload, password, keyFile, embedOptions);
                SaveIndexed(options, stego, output, ExifFor(options, cover, stego.Width, stego.Height), toStdout);
                report = DataReport(cover.Input, StdoutOr(output, toStdout), payload, indexed.ToPixels(), stego.ToPixels(), embedOptions, HidingMode.Password.ToString(), options, StegoEngine.GetCapacity(indexed).ContainerBytes) with { Palette = true };
            }
            else
            {
                var stego = engine.Embed(cover.Pixels, payload, password, keyFile, embedOptions);
                SavePixels(options, stego, output, ExifFor(options, cover, stego.Width, stego.Height), toStdout);
                report = DataReport(cover.Input, StdoutOr(output, toStdout), payload, cover.Pixels, stego, embedOptions, HidingMode.Password.ToString(), options);
            }

            reports.Add(report);
            if (!context.Json)
            {
                PrintSummary(info, report);
            }
        }

        Finish(options, reports);
        return ExitCodes.Success;
    }

    private int ExecuteRecipients(HideOptions options, IReadOnlyList<Cover> covers)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var recipients = CryptoInputs.ParseRecipients(options.Recipients);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        foreach (var cover in covers)
        {
            var capacity = StegoEngine.GetRecipientCapacity(cover.Pixels, recipients.Count, embedOptions).ContainerBytes;
            if (containerSize > capacity)
            {
                throw InsufficientCapacity(cover, containerSize, capacity, embedOptions);
            }
        }

        var engine = Engine();
        return WriteEach(options, covers, cover => engine.Embed(cover.Pixels, payload, recipients, embedOptions), (cover, output, stego) =>
        {
            var capacity = StegoEngine.GetRecipientCapacity(cover.Pixels, recipients.Count, embedOptions).ContainerBytes;
            return DataReport(cover.Input, output, payload, cover.Pixels, stego, embedOptions, HidingMode.Recipients.ToString(), options, capacity) with
            {
                Recipients = recipients.Count,
            };
        });
    }

    private int ExecuteDeniable(HideOptions options, IReadOnlyList<Cover> covers)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var decoyPayload = options.DecoyRandom ? null : BuildPayload(options.DecoyTexts, options.DecoyFiles, []);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        var decoySize = decoyPayload is null ? 0 : StegoEngine.GetContainerSize(decoyPayload);
        foreach (var cover in covers)
        {
            var capacity = StegoEngine.GetDeniableCapacity(cover.Pixels, options.Bits).ContainerBytes;
            if (containerSize > capacity)
            {
                throw InsufficientCapacity(cover, containerSize, capacity, embedOptions, " (each deniable half)");
            }

            if (decoySize > capacity)
            {
                throw InsufficientCapacity(cover, decoySize, capacity, embedOptions, " (each deniable half, decoy)");
            }
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        DeniablePart? decoy = null;
        if (decoyPayload is not null)
        {
            var decoyPassword = context.Passwords.GetPassword(new PasswordPrompt(
                "Decoy password: ", ConsolePasswordProvider.DecoyEnvironmentVariable, Confirm: true, ConfirmLabel: "Confirm decoy password: "));
            decoy = new DeniablePart(decoyPayload, decoyPassword, keyFile);
        }

        var first = new DeniablePart(payload, password, keyFile);
        var engine = Engine();
        return WriteEach(options, covers, cover => Deniable(engine, cover.Pixels, first, decoy, embedOptions), (cover, output, stego) =>
            DataReport(cover.Input, output, payload, cover.Pixels, stego, embedOptions, HidingMode.Deniable.ToString(), options,
                StegoEngine.GetDeniableCapacity(cover.Pixels, options.Bits).ContainerBytes));
    }

    private static PixelBuffer Deniable(StegoEngine engine, PixelBuffer cover, DeniablePart first, DeniablePart? decoy, EmbedOptions embedOptions)
    {
        try
        {
            return engine.EmbedDeniable(cover, first, decoy, embedOptions);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    private int ExecuteSplit(HideOptions options, IReadOnlyList<Cover> covers, int threshold)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var embedOptions = EmbedOptionsFor(options);
        var shareSize = StegoEngine.GetShareContainerSize(payload);
        foreach (var cover in covers)
        {
            RequireFits(cover, shareSize, embedOptions);
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        IReadOnlyList<PixelBuffer> shares;
        try
        {
            shares = engine.EmbedShares(covers.Select(c => c.Pixels).ToList(), payload, threshold, password, keyFile, embedOptions);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }

        var reports = new List<HideReport>(covers.Count);
        for (var i = 0; i < covers.Count; i++)
        {
            var cover = covers[i];
            var output = OutputPathFor(options, cover.Input);
            SavePixels(options, shares[i], output, ExifFor(options, cover, shares[i].Width, shares[i].Height), toStdout: false);
            var capacity = StegoEngine.GetCapacity(cover.Pixels, options.Bits).ContainerBytes;
            var report = DataReport(cover.Input, Path.GetFullPath(output), payload, cover.Pixels, shares[i], embedOptions, "Split", options, capacity) with
            {
                Threshold = threshold,
                ShareCount = covers.Count,
                ShareIndex = i + 1,
            };
            reports.Add(report);
            if (!context.Json)
            {
                context.Out.WriteLine($"Wrote share {i + 1} of {covers.Count} (threshold {threshold}) to {report.Output}");
            }
        }

        Finish(options, reports);
        context.Error.WriteLine($"note: any {threshold} of these {covers.Count} images, with the password, rebuild the data. Keep them apart.");
        return ExitCodes.Success;
    }

    private int ExecuteRobust(HideOptions options, IReadOnlyList<Cover> covers)
    {
        var profile = RobustProfileFor(options.Robust!);
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var payloadSize = StegoEngine.GetRobustPayloadSize(payload);
        foreach (var cover in covers)
        {
            var capacity = StegoEngine.GetRobustCapacity(cover.Width, cover.Height, profile);
            if (payloadSize > capacity)
            {
                throw new CliException(
                    $"{Inputs.DisplayName(cover.Input)}: needs {Format.Bytes(payloadSize)} but this cover holds {Format.Bytes(capacity)} in {profile.Name} robust mode. " +
                    "Use a shorter message or a larger cover; robust mode cannot carry files.",
                    ExitCodes.InsufficientCapacity);
            }
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        var reports = new List<HideReport>(covers.Count);
        var toStdout = IsStdout(options);
        foreach (var cover in covers)
        {
            var stego = engine.EmbedRobust(cover.Pixels, payload, password, profile, keyFile);
            var output = OutputPathFor(options, cover.Input);
            if (toStdout)
            {
                var stdout = context.OpenStandardOutput();
                RobustStego.WriteJpeg(stego, stdout, profile);
                stdout.Flush();
            }
            else
            {
                ImageCodecExtensions.WriteAtomically(output, options.Force, stream => RobustStego.WriteJpeg(stego, stream, profile));
            }

            var capacity = StegoEngine.GetRobustCapacity(cover.Width, cover.Height, profile);
            var report = new HideReport(
                Inputs.DisplayName(cover.Input),
                StdoutOr(output, toStdout),
                "Robust",
                payload.Entries.Count(e => e.Kind == PayloadEntryKind.Text),
                payload.Entries.Count(e => e.Kind == PayloadEntryKind.File),
                payload.ContentLength,
                payloadSize,
                capacity,
                options.Bits,
                capacity == 0 ? 0 : (double)payloadSize / capacity,
                null,
                null,
                null)
            {
                RobustProfile = profile.Name,
                ShrunkWidth = stego.Width,
                ShrunkHeight = stego.Height,
            };
            reports.Add(report);
            if (!context.Json)
            {
                context.Out.WriteLine($"Hidden {Format.Bytes(payloadSize)} in {profile.Name} robust mode, image shrunk to {stego.Width}x{stego.Height}.");
                context.Out.WriteLine($"Wrote {report.Output}");
            }
        }

        if (context.Json)
        {
            JsonOutput.Write<HideReport>(context, reports);
        }

        context.Error.WriteLine($"note: send the JPEG as a normal photo through {profile.Name}. Robust mode is tuned for that one app and holds a few hundred bytes only.");
        return ExitCodes.Success;
    }

    private int WriteEach(HideOptions options, IReadOnlyList<Cover> covers, Func<Cover, PixelBuffer> embed, Func<Cover, string, PixelBuffer, HideReport> report)
    {
        var toStdout = IsStdout(options);
        var info = toStdout ? context.Error : context.Out;
        var reports = new List<HideReport>(covers.Count);
        foreach (var cover in covers)
        {
            var stego = embed(cover);
            var output = OutputPathFor(options, cover.Input);
            SavePixels(options, stego, output, ExifFor(options, cover, stego.Width, stego.Height), toStdout);
            var reported = report(cover, StdoutOr(output, toStdout), stego);
            reports.Add(reported);
            if (!context.Json)
            {
                PrintSummary(info, reported);
            }
        }

        Finish(options, reports);
        return ExitCodes.Success;
    }

    private void SavePixels(HideOptions options, PixelBuffer stego, string output, byte[]? exif, bool toStdout)
    {
        if (!toStdout)
        {
            context.Codec.SaveImage(stego, output, options.Force, exif);
            return;
        }

        var stdout = context.OpenStandardOutput();
        if (exif is not null && options.Format == LosslessFormat.Png)
        {
            using var png = new MemoryStream();
            context.Codec.EncodePng(stego, png);
            stdout.Write(ExifMetadata.AddToPng(png.GetBuffer().AsSpan(0, (int)png.Length), exif));
        }
        else
        {
            LosslessFormats.Encode(context.Codec, stego, options.Format, stdout);
        }

        stdout.Flush();
    }

    private void SaveIndexed(HideOptions options, IndexedImage stego, string output, byte[]? exif, bool toStdout)
    {
        using var png = new MemoryStream();
        IndexedPng.Write(stego, png);
        var bytes = exif is not null
            ? ExifMetadata.AddToPng(png.GetBuffer().AsSpan(0, (int)png.Length), exif)
            : png.ToArray();
        if (toStdout)
        {
            var stdout = context.OpenStandardOutput();
            stdout.Write(bytes);
            stdout.Flush();
        }
        else
        {
            ImageCodecExtensions.WriteAtomically(output, options.Force, stream => stream.Write(bytes));
        }
    }

    private byte[]? ExifFor(HideOptions options, Cover cover, int width, int height)
    {
        if (!options.KeepExif)
        {
            return null;
        }

        var exif = ExifMetadata.ForOutput(cover.Bytes, width, height);
        if (exif is null)
        {
            context.Error.WriteLine($"note: {Inputs.DisplayName(cover.Input)} has no EXIF to keep.");
        }

        return exif;
    }

    private void Finish(HideOptions options, List<HideReport> reports)
    {
        if (context.Json)
        {
            JsonOutput.Write<HideReport>(context, reports);
        }

        if (reports.Any(r => r.Usage > 0.5))
        {
            context.Error.WriteLine("warning: more than half the capacity is used; statistical detection gets easier. A larger or noisier photo helps.");
        }

        if (options.Bits >= 3 && options.Robust is null)
        {
            context.Error.WriteLine("warning: 3-4 bits per channel can leave visible noise in smooth areas such as sky.");
        }

        context.Error.WriteLine("note: send the image as a file/document. Chat apps re-compress photos, which destroys hidden data.");
    }

    private HideReport DataReport(string input, string output, Payload payload, PixelBuffer cover, PixelBuffer stego, EmbedOptions embedOptions, string mode, HideOptions options, long? capacityOverride = null)
    {
        var containerSize = StegoEngine.GetContainerSize(payload);
        var capacity = capacityOverride ?? StegoEngine.GetCapacity(cover, embedOptions).ContainerBytes;
        var diff = ImageMetrics.Compare(cover, stego);
        return new HideReport(
            input,
            output,
            mode,
            payload.Entries.Count(e => e.Kind == PayloadEntryKind.Text),
            payload.Entries.Count(e => e.Kind == PayloadEntryKind.File),
            payload.ContentLength,
            containerSize,
            capacity,
            embedOptions.BitsPerChannel,
            capacity == 0 ? 0 : (double)containerSize / capacity,
            diff.ChangedRatio,
            diff.MaxAbsoluteDelta,
            diff.Psnr)
        {
            KdfProfile = mode == HidingMode.Recipients.ToString() ? null : options.Kdf.ToString(),
            Adaptive = embedOptions.Adaptive,
        };
    }

    private static void PrintSummary(TextWriter writer, HideReport report)
    {
        writer.WriteLine($"Hidden {report.Messages} message(s) and {report.Files} file(s): {Format.Bytes(report.ContentBytes)} → {Format.Bytes(report.StoredBytes)} stored.");
        var palette = report.Palette == true ? " (palette, 1 bit per pixel)" : string.Empty;
        writer.WriteLine($"Used {Format.Percent(report.Usage)} of {Format.Bytes(report.CapacityBytes)} capacity at {report.BitsPerChannel} bit(s) per channel{palette}.");
        if (report.Psnr is { } psnr && report.ChangedRatio is { } changed && report.MaxDelta is { } delta)
        {
            writer.WriteLine($"Changed {Format.Percent(changed)} of colour values (max ±{delta}), PSNR {Format.Number(psnr, "0.0")} dB.");
        }

        writer.WriteLine($"Wrote {report.Output}");
    }

    private StegoEngine Engine() => new(context.Kdf);

    private EmbedOptions EmbedOptionsFor(HideOptions options) =>
        EmbedOptions.Default with { BitsPerChannel = options.Bits, KdfProfile = options.Kdf, Adaptive = options.Adaptive };

    private string AskPassword(HideOptions options, bool confirm) =>
        context.Passwords.GetPassword(new PasswordPrompt("Password: ", Confirm: confirm, FromStdin: options.PasswordFromStdin));

    private static bool IsStdout(HideOptions options) => options.Output is not null && Inputs.IsStandardStream(options.Output);

    private static string StdoutOr(string output, bool toStdout) => toStdout ? "stdout" : Path.GetFullPath(output);

    private List<Cover> DecodeCovers(HideOptions options)
    {
        var covers = new List<Cover>(options.Inputs.Count);
        foreach (var input in options.Inputs)
        {
            var bytes = Inputs.ReadBytes(context, input);
            if (!options.TrueColor && options.Robust is null && IndexedPng.IsIndexed(bytes))
            {
                var indexed = IndexedPng.Read(bytes, context.Codec is SkiaImageCodec ? SkiaImageCodec.DefaultMaxPixels : long.MaxValue);
                covers.Add(new Cover(input, bytes, indexed.ToPixels(), indexed));
            }
            else
            {
                using var stream = new MemoryStream(bytes, writable: false);
                covers.Add(new Cover(input, bytes, context.Codec.Decode(stream, applyOrientation: true), null));
            }
        }

        return covers;
    }

    private static void RequireFits(Cover cover, long needed, EmbedOptions embedOptions)
    {
        if (cover.Indexed is { } indexed)
        {
            var capacity = StegoEngine.GetCapacity(indexed).ContainerBytes;
            if (needed > capacity)
            {
                throw new CliException(
                    $"{Inputs.DisplayName(cover.Input)}: needs {Format.Bytes(needed)} but this palette PNG holds {Format.Bytes(capacity)} (1 bit per pixel). " +
                    "Hide less, or add --truecolor to use all colour channels (the output stops being a palette image).",
                    ExitCodes.InsufficientCapacity);
            }

            return;
        }

        var pixelCapacity = StegoEngine.GetCapacity(cover.Pixels, embedOptions).ContainerBytes;
        if (needed > pixelCapacity)
        {
            throw new CliException($"{Inputs.DisplayName(cover.Input)}: {CapacityAdvice(cover.Pixels, needed, embedOptions)}", ExitCodes.InsufficientCapacity);
        }
    }

    private static CliException InsufficientCapacity(Cover cover, long needed, long capacity, EmbedOptions embedOptions, string note = "")
    {
        var advice = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > embedOptions.BitsPerChannel)
            .FirstOrDefault(b => StegoEngine.GetCapacity(cover.Pixels, embedOptions with { BitsPerChannel = b }).ContainerBytes >= needed, 0);
        var hint = advice > 0 ? $" It would fit with --bits {advice}, or use a larger image." : " Use a larger image (more pixels) or hide less.";
        return new CliException(
            $"{Inputs.DisplayName(cover.Input)}: Needs {Format.Bytes(needed)} but {cover.Width}x{cover.Height} holds {Format.Bytes(Math.Max(0, capacity))} at {embedOptions.BitsPerChannel} bit(s) per channel{note}.{hint}",
            ExitCodes.InsufficientCapacity);
    }

    private static bool IsDeniable(HideOptions options) =>
        options.DecoyRandom || options.DecoyTexts.Count > 0 || options.DecoyFiles.Count > 0;

    private static RobustProfile RobustProfileFor(string name) => name.ToLowerInvariant() switch
    {
        "line" => RobustProfile.Line,
        "telegram" => RobustProfile.Telegram,
        _ => throw new CliException($"Unknown robust profile '{name}'. Use line or telegram."),
    };

    private void Validate(HideOptions options)
    {
        if (options.Inputs.Count == 0)
        {
            throw new CliException("Give at least one cover image with --input.");
        }

        var deniable = IsDeniable(options);
        var recipients = options.Recipients.Count > 0;
        var split = options.Split is not null;
        var robust = options.Robust is not null;

        if (options.Texts.Count + options.TextFiles.Count + options.Files.Count == 0)
        {
            throw new CliException("Nothing to hide: give --text, --text-file and/or --file.");
        }

        var chosen = new List<string>();
        if (recipients)
        {
            chosen.Add("--recipient");
        }

        if (split)
        {
            chosen.Add("--split");
        }

        if (deniable)
        {
            chosen.Add("a decoy (--decoy-text/--decoy-file/--decoy-random)");
        }

        if (robust)
        {
            chosen.Add("--robust");
        }

        if (chosen.Count > 1)
        {
            throw new CliException($"Can't combine {string.Join(" and ", chosen)} in one hide. Pick one.");
        }

        if (recipients)
        {
            if (options.KeyFile is not null)
            {
                throw new CliException("Public-key mode (--recipient) uses no key file; drop --key-file.");
            }

            if (options.PasswordFromStdin)
            {
                throw new CliException("Public-key mode (--recipient) needs no password; drop --password-stdin.");
            }
        }

        if (robust && options.Files.Count > 0)
        {
            throw new CliException("Robust mode carries only text, not files; drop --file.");
        }

        if (robust && options.Adaptive)
        {
            throw new CliException("Robust mode does not use --adaptive.");
        }

        if (deniable && options.Adaptive)
        {
            throw new CliException("Deniable mode fills the whole image, so --adaptive does not apply.");
        }

        if (options.DecoyRandom && (options.DecoyTexts.Count > 0 || options.DecoyFiles.Count > 0))
        {
            throw new CliException("Use either --decoy-random or --decoy-text/--decoy-file, not both.");
        }

        if (options.KeepExif && robust)
        {
            throw new CliException("Robust mode writes a JPEG, which does not carry EXIF here; drop --keep-exif.");
        }

        ValidateOutputs(options, split, robust);
        ValidateStdin(options);
    }

    private void ValidatePalette(HideOptions options, IReadOnlyList<Cover> covers)
    {
        var palette = covers.Where(c => c.IsPalette).ToList();
        if (palette.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", palette.Select(c => Inputs.DisplayName(c.Input)));
        if (options.Recipients.Count > 0 || options.Split is not null || IsDeniable(options))
        {
            throw new CliException($"{names}: palette PNGs support password mode only. Add --truecolor to expand to a true-colour image for this mode.");
        }

        if (options.Bits != 1)
        {
            throw new CliException($"{names}: palette PNGs carry one bit per pixel; drop --bits (or add --truecolor to use all channels).");
        }

        if (options.Adaptive)
        {
            throw new CliException($"{names}: palette PNGs do not support --adaptive. Add --truecolor to use it.");
        }

        if (options.Output is not null && !Inputs.IsStandardStream(options.Output) && LosslessFormats.FromPath(options.Output) != LosslessFormat.Png)
        {
            throw new CliException($"{names}: a palette cover writes a PNG; use a .png output (or --truecolor).");
        }

        if (options.OutputDirectory is not null && options.Format != LosslessFormat.Png)
        {
            throw new CliException($"{names}: a palette cover writes a PNG; use --format Png (or --truecolor).");
        }
    }

    private void ValidateOutputs(HideOptions options, bool split, bool robust)
    {
        if (options.Output is not null && options.OutputDirectory is not null)
        {
            throw new CliException("Use either --output or --output-dir, not both.");
        }

        if (split)
        {
            if (options.Inputs.Count < 2)
            {
                throw new CliException("--split needs at least two cover images (one share per cover).");
            }

            if (options.Split is < 2)
            {
                throw new CliException("--split K needs K of at least 2.");
            }

            if (options.Split > options.Inputs.Count)
            {
                throw new CliException($"--split {options.Split} needs at least {options.Split} covers, but {options.Inputs.Count} were given.");
            }

            if (options.OutputDirectory is null)
            {
                throw new CliException("--split writes one share image per cover, so use --output-dir.");
            }
        }
        else if (options.Inputs.Count > 1 && options.OutputDirectory is null)
        {
            throw new CliException("Several covers need --output-dir (one image is written per cover).");
        }

        if (options.Output is null && options.OutputDirectory is null)
        {
            throw new CliException(robust
                ? "Give --output (a .jpg file, or - for stdout) or --output-dir."
                : "Give --output (a .png file, or - for stdout) or --output-dir.");
        }

        if (robust)
        {
            if (options.Output is not null && !Inputs.IsStandardStream(options.Output) && !IsJpeg(options.Output))
            {
                throw new CliException("Robust mode writes a JPEG; give a .jpg or .jpeg output.");
            }
        }
        else if (options.Output is not null && !Inputs.IsStandardStream(options.Output) && LosslessFormats.FromPath(options.Output) is null)
        {
            throw new CliException("Output must be a lossless image: .png, .webp, .bmp or .tiff. Lossy formats such as JPEG destroy the hidden bits.");
        }

        if (options.KeepExif && !robust)
        {
            var format = options.Output is not null && !Inputs.IsStandardStream(options.Output)
                ? LosslessFormats.FromPath(options.Output)
                : options.Format;
            if (format is not LosslessFormat.Png)
            {
                throw new CliException("--keep-exif only works with PNG output; use a .png output or --format Png.");
            }
        }

        if (options.Output is not null && Inputs.IsStandardStream(options.Output) && context.Json)
        {
            throw new CliException("--json can't be combined with --output - (both would use stdout).");
        }

        if (!options.Force)
        {
            foreach (var input in options.Inputs)
            {
                var output = OutputPathFor(options, input);
                if (!Inputs.IsStandardStream(output) && File.Exists(output))
                {
                    throw new CliException($"{Path.GetFullPath(output)} already exists. Use --force to overwrite.");
                }
            }
        }
    }

    private void ValidateStdin(HideOptions options)
    {
        Inputs.RequireSingleStdinUse(
            options.Inputs.Any(Inputs.IsStandardStream),
            options.TextFiles.Any(Inputs.IsStandardStream),
            options.PasswordFromStdin);
        if (options.Inputs.Count(Inputs.IsStandardStream) + options.TextFiles.Count(Inputs.IsStandardStream) > 1)
        {
            throw new CliException("Only one input can come from standard input.");
        }
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".JPG" or ".JPEG";

    private string OutputPathFor(HideOptions options, string input)
    {
        if (options.OutputDirectory is not { } directory)
        {
            return options.Output!;
        }

        var extension = options.Robust is not null ? ".jpg" : LosslessFormats.Extension(options.Format);
        return Path.Combine(directory, SafeFileName.Sanitize(Inputs.Stem(input) + extension));
    }

    private Payload BuildPayload(IReadOnlyList<string> texts, IReadOnlyList<string> textFiles, IReadOnlyList<string> files)
    {
        var entries = new List<PayloadEntry>();
        entries.AddRange(texts.Select(PayloadEntry.FromText));

        foreach (var textFile in textFiles)
        {
            var bytes = Inputs.ReadBytes(context, textFile);
            try
            {
                entries.Add(PayloadEntry.FromText(new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿')));
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new CliException($"{Inputs.DisplayName(textFile)} is not valid UTF-8 text. Use --file to hide it as a file.");
            }
        }

        foreach (var path in files)
        {
            if (Directory.Exists(path))
            {
                entries.Add(PayloadEntry.FromFile(new DirectoryInfo(path).Name + ".zip", ZipDirectory(path)));
            }
            else
            {
                entries.Add(PayloadEntry.FromFile(Path.GetFileName(path), Inputs.ReadBytes(context, path)));
            }
        }

        return new Payload(entries);
    }

    /// <summary>Folders are hidden as a single .zip so their structure survives.</summary>
    internal static byte[] ZipDirectory(string path)
    {
        using var buffer = new MemoryStream();
        ZipFile.CreateFromDirectory(path, buffer, CompressionLevel.Optimal, includeBaseDirectory: true);
        if (buffer.Length > Inputs.MaxFileBytes)
        {
            throw new CliException($"{path} zips to {ByteSize.Format(buffer.Length)}; the limit is {ByteSize.Format(Inputs.MaxFileBytes)}.");
        }

        return buffer.ToArray();
    }

    private static string CapacityAdvice(PixelBuffer cover, long needed, EmbedOptions embedOptions)
    {
        var bits = embedOptions.BitsPerChannel;
        var options = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > bits && StegoEngine.GetCapacity(cover, embedOptions with { BitsPerChannel = b }).ContainerBytes >= needed)
            .ToList();
        var advice = options.Count > 0
            ? $" It would fit with --bits {options[0]}, or use a larger image."
            : " Use a larger image (more pixels) or hide less.";
        return $"Needs {Format.Bytes(needed)} but {cover.Width}x{cover.Height} holds {Format.Bytes(StegoEngine.GetCapacity(cover, embedOptions).ContainerBytes)} at {bits} bit(s) per channel.{advice}";
    }
}
