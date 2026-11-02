using SweetHide.Core;
using SweetHide.Core.Analysis;

namespace SweetHide.Cli;

internal static class InfoCommands
{
    public static int Capacity(CliContext context, IReadOnlyList<string> inputs)
    {
        var reports = new List<CapacityReport>();
        foreach (var input in inputs)
        {
            var image = Inputs.ReadImage(context, input, applyOrientation: true);
            var rows = StegoEngine.SupportedBitsPerChannel
                .Select(bits => StegoEngine.GetCapacity(image, bits))
                .Select(c => new CapacityRow(c.BitsPerChannel, c.PayloadBytes, c.ContainerBytes))
                .ToList();
            reports.Add(new CapacityReport(Inputs.DisplayName(input), image.Width, image.Height, StegoEngine.GetCapacity(image, 1).UsablePixels, rows));
        }

        if (context.Json)
        {
            JsonOutput.Write<CapacityReport>(context, reports);
            return ExitCodes.Success;
        }

        foreach (var report in reports)
        {
            if (reports.Count > 1)
            {
                context.Out.WriteLine($"== {report.Input}");
            }

            context.Out.WriteLine($"Image: {report.Width} x {report.Height}, {report.UsablePixels:N0} usable pixels");
            context.Out.WriteLine();
            context.Out.WriteLine("bits  capacity     ~Thai chars*  visibility");
            foreach (var row in report.Capacity)
            {
                var visibility = row.BitsPerChannel switch
                {
                    1 => "invisible (default)",
                    2 => "invisible to the eye",
                    3 => "may show in smooth areas",
                    _ => "visible noise",
                };
                context.Out.WriteLine($"{row.BitsPerChannel,4}  {Format.Bytes(row.PayloadBytes),-11}  {row.PayloadBytes / 3,12:N0}  {visibility}");
            }

            context.Out.WriteLine();
        }

        context.Out.WriteLine("* uncompressed UTF-8 (3 bytes per Thai character); text usually compresses 2-4x.");
        return ExitCodes.Success;
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
