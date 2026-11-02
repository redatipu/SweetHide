using System.IO.Compression;

using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

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
    bool PasswordFromStdin);

internal sealed class HideCommand(CliContext context)
{
    public int Execute(HideOptions options)
    {
        Validate(options);
        var payload = BuildPayload(options);
        var containerSize = StegoEngine.GetContainerSize(payload);

        // Decode every cover and check capacity before asking for the password.
        var covers = options.Inputs.Select(input => (Input: input, Pixels: Inputs.ReadImage(context, input, applyOrientation: true))).ToList();
        foreach (var (input, pixels) in covers)
        {
            var capacity = StegoEngine.GetCapacity(pixels, options.Bits);
            if (containerSize > capacity.ContainerBytes)
            {
                throw new CliException($"{Inputs.DisplayName(input)}: {CapacityAdvice(pixels, containerSize, options.Bits)}", ExitCodes.InsufficientCapacity);
            }
        }

        var password = context.Passwords.GetPassword(confirm: true, options.PasswordFromStdin);
        var engine = new StegoEngine(context.Kdf);
        var toStdout = options.Output is not null && Inputs.IsStandardStream(options.Output);
        var info = toStdout ? context.Error : context.Out; // stdout carries the PNG then
        var reports = new List<HideReport>(covers.Count);

        foreach (var (input, pixels) in covers)
        {
            var stego = engine.Embed(pixels, payload, password, options.Bits);
            var output = OutputPathFor(options, input);
            if (toStdout)
            {
                // Not disposed: closing the process's stdout is the runtime's job.
                var stdout = context.OpenStandardOutput();
                context.Codec.EncodePng(stego, stdout);
                stdout.Flush();
            }
            else
            {
                context.Codec.SavePng(stego, output, options.Force);
            }

            var capacity = StegoEngine.GetCapacity(pixels, options.Bits);
            var diff = ImageMetrics.Compare(pixels, stego);
            var report = new HideReport(
                Inputs.DisplayName(input),
                toStdout ? "stdout" : Path.GetFullPath(output),
                payload.Entries.Count(e => e.Kind == PayloadEntryKind.Text),
                payload.Entries.Count(e => e.Kind == PayloadEntryKind.File),
                payload.ContentLength,
                containerSize,
                capacity.ContainerBytes,
                options.Bits,
                (double)containerSize / capacity.ContainerBytes,
                diff.ChangedRatio,
                diff.MaxAbsoluteDelta,
                diff.Psnr);
            reports.Add(report);

            if (!context.Json)
            {
                PrintSummary(info, report);
            }
        }

        if (context.Json)
        {
            JsonOutput.Write<HideReport>(context, reports);
        }

        if (reports.Any(r => r.Usage > 0.5))
        {
            context.Error.WriteLine("warning: more than half the capacity is used; statistical detection gets easier. A larger or noisier photo helps.");
        }

        if (options.Bits >= 3)
        {
            context.Error.WriteLine("warning: 3-4 bits per channel can leave visible noise in smooth areas such as sky.");
        }

        context.Error.WriteLine("note: send the PNG as a file/document. Chat apps re-compress photos, which destroys hidden data.");
        return ExitCodes.Success;
    }

    private static void PrintSummary(TextWriter writer, HideReport report)
    {
        writer.WriteLine($"Hidden {report.Messages} message(s) and {report.Files} file(s): {Format.Bytes(report.ContentBytes)} → {Format.Bytes(report.StoredBytes)} stored.");
        writer.WriteLine($"Used {Format.Percent(report.Usage)} of {Format.Bytes(report.CapacityBytes)} capacity at {report.BitsPerChannel} bit(s) per channel.");
        writer.WriteLine($"Changed {Format.Percent(report.ChangedRatio)} of colour values (max ±{report.MaxDelta}), PSNR {Format.Number(report.Psnr, "0.0")} dB.");
        writer.WriteLine($"Wrote {report.Output}");
    }

    private void Validate(HideOptions options)
    {
        if (options.Texts.Count + options.TextFiles.Count + options.Files.Count == 0)
        {
            throw new CliException("Nothing to hide: give --text, --text-file and/or --file.");
        }

        if (options.Inputs.Count == 0)
        {
            throw new CliException("Give at least one cover image with --input.");
        }

        if (options.Output is not null && options.OutputDirectory is not null)
        {
            throw new CliException("Use either --output or --output-dir, not both.");
        }

        if (options.Inputs.Count > 1 && options.OutputDirectory is null)
        {
            throw new CliException("Several covers need --output-dir (one PNG is written per cover).");
        }

        if (options.Output is null && options.OutputDirectory is null)
        {
            throw new CliException("Give --output (a .png file, or - for stdout) or --output-dir.");
        }

        if (options.Output is not null && !Inputs.IsStandardStream(options.Output)
            && !string.Equals(Path.GetExtension(options.Output), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("Output must be a .png file. Lossy formats such as JPEG destroy the hidden bits.");
        }

        if (options.Output is not null && Inputs.IsStandardStream(options.Output) && context.Json)
        {
            throw new CliException("--json can't be combined with --output - (both would use stdout).");
        }

        Inputs.RequireSingleStdinUse(
            options.Inputs.Any(Inputs.IsStandardStream),
            options.TextFiles.Any(Inputs.IsStandardStream),
            options.PasswordFromStdin);
        if (options.Inputs.Count(Inputs.IsStandardStream) + options.TextFiles.Count(Inputs.IsStandardStream) > 1)
        {
            throw new CliException("Only one input can come from standard input.");
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

    private static string OutputPathFor(HideOptions options, string input) =>
        options.OutputDirectory is { } directory
            ? Path.Combine(directory, SafeFileName.Sanitize(Inputs.Stem(input) + ".png"))
            : options.Output!;

    private Payload BuildPayload(HideOptions options)
    {
        var entries = new List<PayloadEntry>();
        entries.AddRange(options.Texts.Select(PayloadEntry.FromText));

        foreach (var textFile in options.TextFiles)
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

        foreach (var path in options.Files)
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

    private static string CapacityAdvice(PixelBuffer cover, long needed, int bits)
    {
        var options = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > bits && StegoEngine.GetCapacity(cover, b).ContainerBytes >= needed)
            .ToList();
        var advice = options.Count > 0
            ? $" It would fit with --bits {options[0]}, or use a larger image."
            : " Use a larger image (more pixels) or hide less.";
        return $"Needs {Format.Bytes(needed)} but {cover.Width}x{cover.Height} holds {Format.Bytes(StegoEngine.GetCapacity(cover, bits).ContainerBytes)} at {bits} bit(s) per channel.{advice}";
    }
}
