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

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public IReadOnlyList<string> DecoyTexts { get; init; } = [];

    public IReadOnlyList<string> DecoyFiles { get; init; } = [];

    public bool DecoyRandom { get; init; }

    public int? Split { get; init; }

    public string? Robust { get; init; }
}

internal sealed class HideCommand(CliContext context)
{
    public int Execute(HideOptions options)
    {
        Validate(options);

        if (options.Robust is not null)
        {
            return ExecuteRobust(options);
        }

        if (options.Recipients.Count > 0)
        {
            return ExecuteRecipients(options);
        }

        if (options.Split is { } threshold)
        {
            return ExecuteSplit(options, threshold);
        }

        if (IsDeniable(options))
        {
            return ExecuteDeniable(options);
        }

        return ExecutePassword(options);
    }

    private int ExecutePassword(HideOptions options)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        var covers = DecodeCovers(options);
        foreach (var (input, pixels) in covers)
        {
            RequireFits(input, pixels, containerSize, embedOptions);
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        return WriteEach(options, covers, (pixels) => engine.Embed(pixels, payload, password, keyFile, embedOptions), (input, pixels, output, stego) =>
            DataReport(input, output, payload, pixels, stego, embedOptions, HidingMode.Password.ToString(), options));
    }

    private int ExecuteRecipients(HideOptions options)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var recipients = CryptoInputs.ParseRecipients(options.Recipients);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        var covers = DecodeCovers(options);
        foreach (var (input, pixels) in covers)
        {
            var capacity = StegoEngine.GetRecipientCapacity(pixels, recipients.Count, embedOptions).ContainerBytes;
            if (containerSize > capacity)
            {
                throw InsufficientCapacity(input, pixels, containerSize, capacity, embedOptions);
            }
        }

        var engine = Engine();
        return WriteEach(options, covers, (pixels) => engine.Embed(pixels, payload, recipients, embedOptions), (input, pixels, output, stego) =>
        {
            var capacity = StegoEngine.GetRecipientCapacity(pixels, recipients.Count, embedOptions).ContainerBytes;
            return DataReport(input, output, payload, pixels, stego, embedOptions, HidingMode.Recipients.ToString(), options, capacity) with
            {
                Recipients = recipients.Count,
            };
        });
    }

    private int ExecuteDeniable(HideOptions options)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        DeniablePart? decoy = null;
        var decoyPayload = options.DecoyRandom ? null : BuildPayload(options.DecoyTexts, options.DecoyFiles, []);
        var embedOptions = EmbedOptionsFor(options);
        var containerSize = StegoEngine.GetContainerSize(payload);
        var decoySize = decoyPayload is null ? 0 : StegoEngine.GetContainerSize(decoyPayload);
        var covers = DecodeCovers(options);
        foreach (var (input, pixels) in covers)
        {
            var capacity = StegoEngine.GetDeniableCapacity(pixels, options.Bits).ContainerBytes;
            if (containerSize > capacity)
            {
                throw InsufficientCapacity(input, pixels, containerSize, capacity, embedOptions, " (each deniable half)");
            }

            if (decoySize > capacity)
            {
                throw InsufficientCapacity(input, pixels, decoySize, capacity, embedOptions, " (each deniable half, decoy)");
            }
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        if (decoyPayload is not null)
        {
            var decoyPassword = context.Passwords.GetPassword(new PasswordPrompt(
                "Decoy password: ", ConsolePasswordProvider.DecoyEnvironmentVariable, Confirm: true, ConfirmLabel: "Confirm decoy password: "));
            decoy = new DeniablePart(decoyPayload, decoyPassword, keyFile);
        }

        var first = new DeniablePart(payload, password, keyFile);
        var engine = Engine();
        return WriteEach(options, covers, (pixels) => Deniable(engine, pixels, first, decoy, embedOptions), (input, pixels, output, stego) =>
            DataReport(input, output, payload, pixels, stego, embedOptions, HidingMode.Deniable.ToString(), options,
                StegoEngine.GetDeniableCapacity(pixels, options.Bits).ContainerBytes));
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

    private int ExecuteSplit(HideOptions options, int threshold)
    {
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var embedOptions = EmbedOptionsFor(options);
        var covers = DecodeCovers(options);
        var shareSize = StegoEngine.GetShareContainerSize(payload);
        foreach (var (input, pixels) in covers)
        {
            RequireFits(input, pixels, shareSize, embedOptions);
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
            var (input, pixels) = covers[i];
            var output = OutputPathFor(options, input);
            context.Codec.SaveImage(shares[i], output, options.Force);
            var capacity = StegoEngine.GetCapacity(pixels, options.Bits).ContainerBytes;
            var report = DataReport(input, Path.GetFullPath(output), payload, pixels, shares[i], embedOptions, "Split", options, capacity) with
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

    private int ExecuteRobust(HideOptions options)
    {
        var profile = RobustProfileFor(options.Robust!);
        var payload = BuildPayload(options.Texts, options.TextFiles, options.Files);
        var payloadSize = StegoEngine.GetRobustPayloadSize(payload);
        var covers = DecodeCovers(options);
        foreach (var (input, pixels) in covers)
        {
            var capacity = StegoEngine.GetRobustCapacity(pixels.Width, pixels.Height, profile);
            if (payloadSize > capacity)
            {
                throw new CliException(
                    $"{Inputs.DisplayName(input)}: needs {Format.Bytes(payloadSize)} but this cover holds {Format.Bytes(capacity)} in {profile.Name} robust mode. " +
                    "Use a shorter message or a larger cover; robust mode cannot carry files.",
                    ExitCodes.InsufficientCapacity);
            }
        }

        var password = AskPassword(options, confirm: true);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        var reports = new List<HideReport>(covers.Count);
        var toStdout = options.Output is not null && Inputs.IsStandardStream(options.Output);
        foreach (var (input, pixels) in covers)
        {
            var stego = engine.EmbedRobust(pixels, payload, password, profile, keyFile);
            var output = OutputPathFor(options, input);
            if (toStdout)
            {
                var stdout = context.OpenStandardOutput();
                RobustStego.WriteJpeg(stego, stdout, profile);
                stdout.Flush();
            }
            else
            {
                WriteJpegFile(stego, output, profile, options.Force);
            }

            var capacity = StegoEngine.GetRobustCapacity(pixels.Width, pixels.Height, profile);
            var report = new HideReport(
                Inputs.DisplayName(input),
                toStdout ? "stdout" : Path.GetFullPath(output),
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

    private int WriteEach(HideOptions options, IReadOnlyList<(string Input, PixelBuffer Pixels)> covers, Func<PixelBuffer, PixelBuffer> embed, Func<string, PixelBuffer, string, PixelBuffer, HideReport> report)
    {
        var toStdout = options.Output is not null && Inputs.IsStandardStream(options.Output);
        var info = toStdout ? context.Error : context.Out;
        var reports = new List<HideReport>(covers.Count);
        foreach (var (input, pixels) in covers)
        {
            var stego = embed(pixels);
            var output = OutputPathFor(options, input);
            if (toStdout)
            {
                var stdout = context.OpenStandardOutput();
                LosslessFormats.Encode(context.Codec, stego, options.Format, stdout);
                stdout.Flush();
            }
            else
            {
                context.Codec.SaveImage(stego, output, options.Force);
            }

            var reported = report(input, pixels, toStdout ? "stdout" : Path.GetFullPath(output), stego);
            reports.Add(reported);
            if (!context.Json)
            {
                PrintSummary(info, reported);
            }
        }

        Finish(options, reports);
        return ExitCodes.Success;
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
        writer.WriteLine($"Used {Format.Percent(report.Usage)} of {Format.Bytes(report.CapacityBytes)} capacity at {report.BitsPerChannel} bit(s) per channel.");
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

    private List<(string Input, PixelBuffer Pixels)> DecodeCovers(HideOptions options) =>
        options.Inputs.Select(input => (Input: input, Pixels: Inputs.ReadImage(context, input, applyOrientation: true))).ToList();

    private static void RequireFits(string input, PixelBuffer cover, long needed, EmbedOptions embedOptions)
    {
        var capacity = StegoEngine.GetCapacity(cover, embedOptions).ContainerBytes;
        if (needed > capacity)
        {
            throw new CliException($"{Inputs.DisplayName(input)}: {CapacityAdvice(cover, needed, embedOptions)}", ExitCodes.InsufficientCapacity);
        }
    }

    private static CliException InsufficientCapacity(string input, PixelBuffer cover, long needed, long capacity, EmbedOptions embedOptions, string note = "")
    {
        var advice = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > embedOptions.BitsPerChannel)
            .FirstOrDefault(b => StegoEngine.GetCapacity(cover, embedOptions with { BitsPerChannel = b }).ContainerBytes >= needed, 0);
        var hint = advice > 0 ? $" It would fit with --bits {advice}, or use a larger image." : " Use a larger image (more pixels) or hide less.";
        return new CliException(
            $"{Inputs.DisplayName(input)}: Needs {Format.Bytes(needed)} but {cover.Width}x{cover.Height} holds {Format.Bytes(Math.Max(0, capacity))} at {embedOptions.BitsPerChannel} bit(s) per channel{note}.{hint}",
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

    private void WriteJpegFile(PixelBuffer image, string path, RobustProfile profile, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new CliException($"Invalid output path: {path}");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                RobustStego.WriteJpeg(image, stream, profile);
            }

            File.Move(temp, full, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }

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

        var secretCount = options.Texts.Count + options.TextFiles.Count + options.Files.Count;
        if (secretCount == 0)
        {
            throw new CliException("Nothing to hide: give --text, --text-file and/or --file.");
        }

        // Mode exclusivity: recipients, split, deniable, robust don't mix with each other.
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

        if (deniable && options.Adaptive)
        {
            throw new CliException("Deniable mode fills the whole image, so --adaptive does not apply.");
        }

        if (options.DecoyRandom && (options.DecoyTexts.Count > 0 || options.DecoyFiles.Count > 0))
        {
            throw new CliException("Use either --decoy-random or --decoy-text/--decoy-file, not both.");
        }

        ValidateOutputs(options, split, robust);
        ValidateStdin(options);
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
