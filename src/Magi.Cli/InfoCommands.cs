using Magi.Core;
using Magi.Core.Analysis;
using Magi.Core.Imaging;

namespace Magi.Cli;

internal static class InfoCommands
{
    public static int Capacity(CliContext context, FileInfo input)
    {
        var image = Decode(context, input, applyOrientation: true);
        var first = StegoEngine.GetCapacity(image, 1);
        context.Out.WriteLine($"Image: {image.Width} x {image.Height}, {first.UsablePixels:N0} usable pixels");
        context.Out.WriteLine();
        context.Out.WriteLine("bits  capacity     ~Thai chars*  visibility");
        foreach (var bits in StegoEngine.SupportedBitsPerChannel)
        {
            var capacity = StegoEngine.GetCapacity(image, bits);
            var visibility = bits switch
            {
                1 => "invisible (default)",
                2 => "invisible to the eye",
                3 => "may show in smooth areas",
                _ => "visible noise",
            };
            context.Out.WriteLine($"{bits,4}  {Format.Bytes(capacity.PayloadBytes),-11}  {capacity.PayloadBytes / 3,12:N0}  {visibility}");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("* uncompressed UTF-8 (3 bytes per Thai character); text usually compresses 2-4x.");
        return ExitCodes.Success;
    }

    public static int Analyze(CliContext context, FileInfo input, FileInfo? planeOutput, int bit)
    {
        var image = Decode(context, input, applyOrientation: false);
        var report = LsbAnalyzer.Analyze(image);

        context.Out.WriteLine($"Image: {report.Width} x {report.Height}");
        context.Out.WriteLine();
        context.Out.WriteLine("channel  LSB=1 ratio  chi-square  dof  p(LSB replacement)");
        foreach (var c in report.Channels)
        {
            context.Out.WriteLine(
                $"{c.Channel,7}  {Format.Number(c.OnesRatio, "0.0000"),11}  {Format.Number(c.ChiSquare, "0.0"),10}  {c.DegreesOfFreedom,3}  {Format.Number(c.EmbeddingProbability, "0.0000"),18}");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("p close to 1 on all channels suggests heavy LSB replacement. Magi uses LSB matching, which this test");
        context.Out.WriteLine("is not designed to detect: try it on images made by other tools and compare.");

        if (planeOutput is not null)
        {
            using var stream = planeOutput.Create();
            context.Codec.EncodePng(LsbAnalyzer.RenderBitPlane(image, bit), stream);
            context.Out.WriteLine($"Wrote bit plane {bit} to {planeOutput.FullName}");
        }

        return ExitCodes.Success;
    }

    private static PixelBuffer Decode(CliContext context, FileInfo input, bool applyOrientation)
    {
        using var stream = input.OpenRead();
        return context.Codec.Decode(stream, applyOrientation);
    }
}
