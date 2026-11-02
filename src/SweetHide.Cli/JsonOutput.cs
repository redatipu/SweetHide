using System.Text.Json;
using System.Text.Json.Serialization;

using SweetHide.Core.Analysis;

namespace SweetHide.Cli;

internal sealed record HideReport(
    string Input,
    string Output,
    int Messages,
    int Files,
    long ContentBytes,
    long StoredBytes,
    long CapacityBytes,
    int BitsPerChannel,
    double Usage,
    double ChangedRatio,
    int MaxDelta,
    double Psnr);

internal sealed record RevealedFile(string Name, long Size, string? SavedTo);

internal sealed record RevealReport(string Input, IReadOnlyList<string> Messages, IReadOnlyList<RevealedFile> Files);

internal sealed record CapacityRow(int BitsPerChannel, long PayloadBytes, long ContainerBytes);

internal sealed record CapacityReport(string Input, int Width, int Height, long UsablePixels, IReadOnlyList<CapacityRow> Capacity);

internal sealed record AnalyzeReport(string Input, int Width, int Height, IReadOnlyList<ChannelLsbStats> Channels, string? BitPlaneImage);

internal sealed record ErrorReport(string Error, int ExitCode);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(IReadOnlyList<HideReport>))]
[JsonSerializable(typeof(IReadOnlyList<RevealReport>))]
[JsonSerializable(typeof(IReadOnlyList<CapacityReport>))]
[JsonSerializable(typeof(IReadOnlyList<AnalyzeReport>))]
[JsonSerializable(typeof(ErrorReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

internal static class JsonOutput
{
    /// <summary>Every command prints a JSON array (one element per input) so scripts can rely on one shape.</summary>
    public static void Write<T>(CliContext context, IReadOnlyList<T> results)
    {
        var typeInfo = CliJsonContext.Default.GetTypeInfo(typeof(IReadOnlyList<T>))
            ?? throw new InvalidOperationException($"No JSON metadata for {typeof(T).Name}.");
        context.Out.WriteLine(JsonSerializer.Serialize(results, typeInfo));
    }

    public static void WriteError(CliContext context, string message, int exitCode) =>
        context.Out.WriteLine(JsonSerializer.Serialize(new ErrorReport(message, exitCode), CliJsonContext.Default.ErrorReport));
}
