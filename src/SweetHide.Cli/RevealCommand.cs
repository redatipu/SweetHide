using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Cli;

internal sealed record RevealOptions(IReadOnlyList<string> Inputs, DirectoryInfo OutputDirectory, bool ListOnly, bool Force, bool PasswordFromStdin)
{
    public string? KeyFile { get; init; }

    public string? Identity { get; init; }
}

internal sealed class RevealCommand(CliContext context)
{
    public int Execute(RevealOptions options)
    {
        Inputs.RequireSingleStdinUse(options.Inputs.Any(Inputs.IsStandardStream), options.PasswordFromStdin);
        if (options.Inputs.Count(Inputs.IsStandardStream) > 1)
        {
            throw new CliException("Only one input can come from standard input.");
        }

        if (options.Identity is not null && options.KeyFile is not null)
        {
            throw new CliException("An identity file reveals public-key data; it takes no key file. Drop --key-file.");
        }

        if (options.Identity is not null && options.PasswordFromStdin)
        {
            throw new CliException("An identity file is unlocked by its passphrase, not --password-stdin. Set " + ConsolePasswordProvider.IdentityEnvironmentVariable + " or let it prompt.");
        }

        var images = options.Inputs
            .Select(input => (Input: input, Bytes: Inputs.ReadBytes(context, input)))
            .Select(x => (x.Input, x.Bytes, Pixels: Decode(x.Bytes)))
            .ToList();

        return options.Identity is not null ? RevealWithIdentity(options, images) : RevealWithPassword(options, images);
    }

    private int RevealWithIdentity(RevealOptions options, List<(string Input, byte[] Bytes, PixelBuffer Pixels)> images)
    {
        using var identity = CryptoInputs.LoadIdentity(context, options.Identity!, options.PasswordFromStdin);
        var engine = Engine();
        var reports = new List<RevealReport>();
        var failures = new List<StegoException>();
        foreach (var (input, _, pixels) in images)
        {
            try
            {
                var result = engine.Reveal(pixels, identity);
                reports.Add(Output(options, input, result.Payload!, result.Info, images.Count > 1));
            }
            catch (StegoException ex)
            {
                Report(options, input, ex, failures, reports);
            }
        }

        return Finish(options, reports, failures);
    }

    private int RevealWithPassword(RevealOptions options, List<(string Input, byte[] Bytes, PixelBuffer Pixels)> images)
    {
        var password = context.Passwords.GetPassword(new PasswordPrompt("Password: ", FromStdin: options.PasswordFromStdin));
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = Engine();
        var reports = new List<RevealReport>();
        var failures = new List<StegoException>();
        var shares = new List<(string Input, ShamirShare Share)>();

        foreach (var (input, bytes, pixels) in images)
        {
            try
            {
                var result = RevealOne(engine, bytes, pixels, password, keyFile);
                if (result.Share is { } share)
                {
                    shares.Add((input, share));
                    if (!context.Json)
                    {
                        context.Error.WriteLine($"{Inputs.DisplayName(input)}: share {share.Index} of a {share.Threshold}-of-{share.ShareCount} split (id {share.SplitId[..8]}…).");
                    }

                    reports.Add(new RevealReport(Inputs.DisplayName(input), ShareInfo(share), [], []));
                    continue;
                }

                reports.Add(Output(options, input, result.Payload!, result.Info, images.Count > 1));
            }
            catch (StegoException normal) when (normal.Error == StegoError.NotFoundOrWrongPassword)
            {
                if (TryRobust(engine, input, pixels, password, keyFile, options, reports, failures, images.Count > 1))
                {
                    continue;
                }

                Report(options, input, normal, failures, reports);
            }
            catch (StegoException ex)
            {
                Report(options, input, ex, failures, reports);
            }
        }

        CombineShares(options, shares, reports, failures);
        return Finish(options, reports, failures);
    }

    /// <summary>Palette carrier first for indexed PNGs, then the ordinary RGBA path.</summary>
    private RevealResult RevealOne(StegoEngine engine, byte[] bytes, PixelBuffer pixels, string password, KeyFile? keyFile)
    {
        if (IndexedPng.IsIndexed(bytes))
        {
            try
            {
                var indexed = IndexedPng.Read(bytes, context.Codec is SkiaImageCodec ? SkiaImageCodec.DefaultMaxPixels : long.MaxValue);
                return engine.Reveal(indexed, password, keyFile);
            }
            catch (StegoException ex) when (ex.Error == StegoError.NotFoundOrWrongPassword)
            {
                // Not hidden in the palette; maybe it is a plain LSB embed in the expanded pixels.
            }
        }

        return engine.Reveal(pixels, password, keyFile);
    }

    private bool TryRobust(StegoEngine engine, string input, PixelBuffer pixels, string password, KeyFile? keyFile, RevealOptions options, List<RevealReport> reports, List<StegoException> failures, bool moreThanOne)
    {
        RobustRevealResult robust;
        try
        {
            robust = engine.RevealRobust(pixels, password, keyFile);
        }
        catch (StegoException ex)
        {
            // Robust data that decodes but fails authentication: report it as damaged, not "nothing found".
            Report(options, input, ex, failures, reports);
            return true;
        }

        if (robust.Status == RobustStatus.Found)
        {
            if (!context.Json)
            {
                context.Error.WriteLine(
                    $"{Inputs.DisplayName(input)}: found in {robust.Profile?.Name} robust mode (raw bit error rate {Format.Percent(robust.RawBitErrorRate)}).");
            }

            var info = new HiddenInfoReport("Robust") { RobustProfile = robust.Profile?.Name, RawBitErrorRate = robust.RawBitErrorRate };
            reports.Add(Output(options, input, robust.Payload!, info, moreThanOne));
            return true;
        }

        if (robust.Status == RobustStatus.Damaged)
        {
            Report(options, input, new StegoException(
                StegoError.Corrupted,
                $"Robust-mode data was found in {robust.Profile?.Name} mode but is too damaged to recover (raw bit error rate {Format.Percent(robust.RawBitErrorRate)})."), failures, reports);
            return true;
        }

        return false; // genuinely nothing for this password, in either mode
    }

    private void CombineShares(RevealOptions options, List<(string Input, ShamirShare Share)> shares, List<RevealReport> reports, List<StegoException> failures)
    {
        if (shares.Count == 0)
        {
            return;
        }

        foreach (var group in shares.GroupBy(s => s.Share.SplitId))
        {
            try
            {
                var payload = StegoEngine.CombineShares(group.Select(s => s.Share));
                var inputs = group.Select(s => Inputs.DisplayName(s.Input)).ToList();
                if (!context.Json)
                {
                    context.Out.WriteLine($"== combined from {inputs.Count} share(s): {string.Join(", ", inputs)}");
                }

                var first = group.First().Share;
                var info = new HiddenInfoReport("Split")
                {
                    ShareIndex = null,
                    Threshold = first.Threshold,
                    ShareCount = first.ShareCount,
                    SplitId = first.SplitId,
                };
                reports.Add(Reveal($"combined ({string.Join(", ", inputs)})", payload, info, options.OutputDirectory.FullName, options, labelOutput: false));
            }
            catch (StegoException ex)
            {
                failures.Add(ex);
                if (!context.Json)
                {
                    context.Error.WriteLine($"shares (split {group.Key[..8]}…): {ex.Message}");
                }

                reports.Add(new RevealReport($"split {group.Key[..8]}", null, [], []) { Error = ex.Message });
            }
        }
    }

    private RevealReport Output(RevealOptions options, string input, Payload payload, HiddenDataInfo info, bool moreThanOne)
    {
        var directory = moreThanOne
            ? Path.Combine(options.OutputDirectory.FullName, SafeFileName.Sanitize(Inputs.Stem(input)))
            : options.OutputDirectory.FullName;
        return Reveal(Inputs.DisplayName(input), payload, InfoOf(info), directory, options, moreThanOne);
    }

    private RevealReport Output(RevealOptions options, string input, Payload payload, HiddenInfoReport info, bool moreThanOne)
    {
        var directory = moreThanOne
            ? Path.Combine(options.OutputDirectory.FullName, SafeFileName.Sanitize(Inputs.Stem(input)))
            : options.OutputDirectory.FullName;
        return Reveal(Inputs.DisplayName(input), payload, info, directory, options, moreThanOne);
    }

    private RevealReport Reveal(string input, Payload payload, HiddenInfoReport info, string directory, RevealOptions options, bool labelOutput)
    {
        var messages = new List<string>();
        var files = new List<RevealedFile>();
        var index = 0;
        if (labelOutput && !context.Json)
        {
            context.Out.WriteLine($"== {input}");
        }

        if (!context.Json)
        {
            context.Error.WriteLine($"  {DescribeInfo(info)}");
        }

        foreach (var entry in payload.Entries)
        {
            index++;
            if (entry.Kind == PayloadEntryKind.Text)
            {
                var text = entry.GetText();
                messages.Add(text);
                if (!context.Json)
                {
                    context.Out.WriteLine(options.ListOnly ? $"[{index}] message, {Format.Bytes(entry.Length)}" : text);
                }

                continue;
            }

            var safeName = SafeFileName.Sanitize(entry.Name);
            string? savedTo = null;
            if (options.ListOnly)
            {
                if (!context.Json)
                {
                    context.Out.WriteLine($"[{index}] file \"{safeName}\", {Format.Bytes(entry.Length)}");
                }
            }
            else
            {
                savedTo = SafeFileWriter.Write(directory, entry.Name, entry.Content.Span, options.Force);
                context.Error.WriteLine($"Saved {savedTo} ({Format.Bytes(entry.Length)})");
            }

            files.Add(new RevealedFile(safeName, entry.Length, savedTo));
        }

        return new RevealReport(input, info, messages, files);
    }

    private void Report(RevealOptions options, string input, StegoException ex, List<StegoException> failures, List<RevealReport> reports)
    {
        failures.Add(ex);
        if (!context.Json && options.Inputs.Count > 1)
        {
            context.Error.WriteLine($"{Inputs.DisplayName(input)}: {ex.Message}");
        }

        reports.Add(new RevealReport(Inputs.DisplayName(input), null, [], []) { Error = ex.Message });
    }

    private int Finish(RevealOptions options, List<RevealReport> reports, List<StegoException> failures)
    {
        if (context.Json)
        {
            JsonOutput.Write<RevealReport>(context, reports);
        }

        var revealed = reports.Any(r => r.Error is null && (r.Messages.Count > 0 || r.Files.Count > 0 || (r.Info?.ShareIndex is not null)));
        if (failures.Count == 0)
        {
            return ExitCodes.Success;
        }

        if (revealed)
        {
            return ExitCodes.Success; // something was revealed; other images just had nothing
        }

        if (!context.Json && options.Inputs.Count == 1 && reports.All(r => r.Error is not null))
        {
            context.Error.WriteLine($"error: {failures[0].Message}");
        }

        return CliApp.ExitCodeFor(failures[0]);
    }

    private static HiddenInfoReport InfoOf(HiddenDataInfo info) => new(info.Mode.ToString())
    {
        FormatVersion = info.FormatVersion,
        BitsPerChannel = info.BitsPerChannel,
        Adaptive = info.Adaptive,
        KdfProfile = info.KdfProfile?.ToString(),
    };

    private static HiddenInfoReport ShareInfo(ShamirShare share) => new("Split")
    {
        FormatVersion = 2,
        ShareIndex = share.Index,
        Threshold = share.Threshold,
        ShareCount = share.ShareCount,
        SplitId = share.SplitId,
    };

    private static string DescribeInfo(HiddenInfoReport info)
    {
        var parts = new List<string>();
        if (info.FormatVersion is { } version)
        {
            parts.Add($"format v{version}");
        }

        parts.Add(info.Mode.ToLowerInvariant() + " mode");
        if (info.RobustProfile is { } profile)
        {
            parts.Add($"profile {profile}");
        }

        if (info.KdfProfile is { } kdf)
        {
            parts.Add($"{kdf} KDF");
        }

        if (info.BitsPerChannel is { } bits)
        {
            parts.Add($"{bits} bit(s)/channel");
        }

        if (info.Adaptive == true)
        {
            parts.Add("adaptive");
        }

        if (info.ShareIndex is { } shareIndex)
        {
            parts.Add($"share {shareIndex} of {info.ShareCount} (need {info.Threshold})");
        }

        if (info.RawBitErrorRate is { } rate)
        {
            parts.Add($"raw bit errors {Format.Percent(rate)}");
        }

        return string.Join(", ", parts);
    }

    private StegoEngine Engine() => new(context.Kdf);

    private PixelBuffer Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return context.Codec.Decode(stream, applyOrientation: false);
    }
}
