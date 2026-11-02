using SweetHide.Core;
using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Core.Robust;

namespace SweetHide.Cli;

internal static class InfoCommands
{
    public static int Capacity(CliContext context, IReadOnlyList<string> inputs, int? recipients)
    {
        var reports = new List<CapacityReport>();
        foreach (var input in inputs)
        {
            reports.Add(Measure(context, input, recipients));
        }

        if (context.Json)
        {
            JsonOutput.Write<CapacityReport>(context, reports);
            return ExitCodes.Success;
        }

        foreach (var report in reports)
        {
            Print(context, report, reports.Count > 1, recipients);
        }

        context.Out.WriteLine("* uncompressed UTF-8 (3 bytes per Thai character); text usually compresses 2-4x.");
        return ExitCodes.Success;
    }

    private static CapacityReport Measure(CliContext context, string input, int? recipients)
    {
        var bytes = Inputs.ReadBytes(context, input);
        IndexedImage? indexed = IndexedPng.IsIndexed(bytes)
            ? IndexedPng.Read(bytes, context.Codec is SkiaImageCodec ? SkiaImageCodec.DefaultMaxPixels : long.MaxValue)
            : null;
        using var stream = new MemoryStream(bytes, writable: false);
        var image = indexed?.ToPixels() ?? context.Codec.Decode(stream, applyOrientation: true);

        var rows = indexed is not null
            ? new List<CapacityRow> { Row(StegoEngine.GetCapacity(indexed)) }
            : StegoEngine.SupportedBitsPerChannel.Select(bits => Row(StegoEngine.GetCapacity(image, bits))).ToList();

        var robust = RobustProfile.All.Select(profile =>
        {
            var (w, h) = RobustStego.TargetSize(image.Width, image.Height, profile);
            return new RobustCapacityRow(profile.Name, StegoEngine.GetRobustCapacity(image.Width, image.Height, profile), w, h);
        }).ToList();

        var assessment = CoverAnalyzer.Assess(image);
        var cover = new CoverScoreReport(assessment.Score, assessment.Rating.ToString(), assessment.Issues.Select(i => i.ToString()).ToList());

        return new CapacityReport(Inputs.DisplayName(input), image.Width, image.Height, StegoEngine.GetCapacity(image, 1).UsablePixels, rows)
        {
            Palette = indexed is not null,
            DeniableContainerBytes = indexed is null ? StegoEngine.GetDeniableCapacity(image, 1).ContainerBytes : null,
            RecipientContainerBytes = indexed is null && recipients is { } n ? StegoEngine.GetRecipientCapacity(image, n).ContainerBytes : null,
            Recipients = indexed is null ? recipients : null,
            Robust = robust,
            Cover = cover,
        };
    }

    private static CapacityRow Row(CapacityInfo c) => new(c.BitsPerChannel, c.PayloadBytes, c.ContainerBytes);

    private static void Print(CliContext context, CapacityReport report, bool several, int? recipients)
    {
        if (several)
        {
            context.Out.WriteLine($"== {report.Input}");
        }

        context.Out.WriteLine($"Image: {report.Width} x {report.Height}, {report.UsablePixels:N0} usable pixels{(report.Palette ? " (palette PNG, 1 bit per pixel)" : string.Empty)}");
        context.Out.WriteLine();
        context.Out.WriteLine("bits  capacity     ~Thai chars*  visibility");
        foreach (var row in report.Capacity)
        {
            var visibility = report.Palette ? "palette (1 bit/pixel)" : row.BitsPerChannel switch
            {
                1 => "invisible (default)",
                2 => "invisible to the eye",
                3 => "may show in smooth areas",
                _ => "visible noise",
            };
            context.Out.WriteLine($"{row.BitsPerChannel,4}  {Format.Bytes(row.PayloadBytes),-11}  {row.PayloadBytes / 3,12:N0}  {visibility}");
        }

        context.Out.WriteLine();
        if (report.DeniableContainerBytes is { } deniable)
        {
            context.Out.WriteLine($"Deniable mode: {Format.Bytes(Math.Max(0, deniable - StegoEngine.ContainerOverhead))} per payload (two payloads, 1 bit).");
        }

        if (report.RecipientContainerBytes is { } recipient && recipients is { } count)
        {
            context.Out.WriteLine($"Public-key mode ({count} recipient(s)): {Format.Bytes(Math.Max(0, recipient - StegoEngine.ContainerOverhead))} at 1 bit.");
        }

        if (report.Robust is { } robustRows)
        {
            context.Out.WriteLine("Robust mode (send as a chat photo):");
            foreach (var r in robustRows)
            {
                context.Out.WriteLine($"  {r.Profile,-9} {Format.Bytes(r.PayloadBytes),-10} shrinks to {r.ShrunkWidth}x{r.ShrunkHeight}");
            }
        }

        if (report.Cover is { } cover)
        {
            context.Out.WriteLine($"Cover suitability: {cover.Score}/100 ({cover.Rating}){(cover.Issues.Count > 0 ? " - " + string.Join(", ", cover.Issues) : string.Empty)}");
        }

        context.Out.WriteLine();
    }

    public static int Analyze(CliContext context, IReadOnlyList<string> inputs, FileInfo? planeOutput, int bit)
    {
        if (planeOutput is not null && inputs.Count > 1)
        {
            throw new CliException("--bit-plane works with a single --input.");
        }

        var reports = new List<AnalyzeReport>();
        foreach (var input in inputs)
        {
            var image = Inputs.ReadImage(context, input, applyOrientation: false);
            var report = LsbAnalyzer.Analyze(image);
            string? planePath = null;
            if (planeOutput is not null)
            {
                using var stream = planeOutput.Create();
                context.Codec.EncodePng(LsbAnalyzer.RenderBitPlane(image, bit), stream);
                planePath = planeOutput.FullName;
            }

            reports.Add(new AnalyzeReport(Inputs.DisplayName(input), report.Width, report.Height, report.Channels, planePath));
        }

        if (context.Json)
        {
            JsonOutput.Write<AnalyzeReport>(context, reports);
            return ExitCodes.Success;
        }

        foreach (var report in reports)
        {
            if (reports.Count > 1)
            {
                context.Out.WriteLine($"== {report.Input}");
            }

            context.Out.WriteLine($"Image: {report.Width} x {report.Height}");
            context.Out.WriteLine();
            context.Out.WriteLine("channel  LSB=1 ratio  chi-square  dof  p(LSB replacement)");
            foreach (var c in report.Channels)
            {
                context.Out.WriteLine(
                    $"{c.Channel,7}  {Format.Number(c.OnesRatio, "0.0000"),11}  {Format.Number(c.ChiSquare, "0.0"),10}  {c.DegreesOfFreedom,3}  {Format.Number(c.EmbeddingProbability, "0.0000"),18}");
            }

            if (report.BitPlaneImage is not null)
            {
                context.Out.WriteLine($"Wrote bit plane {bit} to {report.BitPlaneImage}");
            }

            context.Out.WriteLine();
        }

        context.Out.WriteLine("p close to 1 on all channels suggests heavy LSB replacement. SweetHide uses LSB matching, which this test");
        context.Out.WriteLine("is not designed to detect: try it on images made by other tools and compare.");
        return ExitCodes.Success;
    }
}
