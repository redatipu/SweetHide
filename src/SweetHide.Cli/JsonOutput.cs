using System.Text.Json;
using System.Text.Json.Serialization;

using SweetHide.Core.Analysis;

namespace SweetHide.Cli;

internal sealed record HideReport(
    string Input,
    string Output,
    string Mode,
    int Messages,
    int Files,
    long ContentBytes,
    long StoredBytes,
    long CapacityBytes,
    int BitsPerChannel,
    double Usage,
    double? ChangedRatio,
    int? MaxDelta,
    double? Psnr)
{
    public string? KdfProfile { get; init; }

    public bool? Adaptive { get; init; }

    public bool? Palette { get; init; }

    public int? Recipients { get; init; }

    public int? Threshold { get; init; }

    public int? ShareCount { get; init; }

    public int? ShareIndex { get; init; }

    public string? RobustProfile { get; init; }

    public int? ShrunkWidth { get; init; }

    public int? ShrunkHeight { get; init; }
}

/// <summary>How a revealed image's data was hidden, flattened for JSON.</summary>
internal sealed record HiddenInfoReport(string Mode)
{
    public int? FormatVersion { get; init; }

    public int? BitsPerChannel { get; init; }

    public bool? Adaptive { get; init; }

    public string? KdfProfile { get; init; }

    public string? RobustProfile { get; init; }

    public double? RawBitErrorRate { get; init; }

    public int? ShareIndex { get; init; }

    public int? Threshold { get; init; }

    public int? ShareCount { get; init; }

    public string? SplitId { get; init; }
}

internal sealed record RevealedFile(string Name, long Size, string? SavedTo);

internal sealed record RevealReport(string Input, HiddenInfoReport? Info, IReadOnlyList<string> Messages, IReadOnlyList<RevealedFile> Files)
{
    public string? Error { get; init; }
}

internal sealed record CapacityRow(int BitsPerChannel, long PayloadBytes, long ContainerBytes);

internal sealed record RobustCapacityRow(string Profile, int PayloadBytes, int ShrunkWidth, int ShrunkHeight);

internal sealed record CoverScoreReport(int Score, string Rating, IReadOnlyList<string> Issues);

internal sealed record CapacityReport(string Input, int Width, int Height, long UsablePixels, IReadOnlyList<CapacityRow> Capacity)
{
    public bool Palette { get; init; }

    public long? DeniableContainerBytes { get; init; }

    public long? RecipientContainerBytes { get; init; }

    public int? Recipients { get; init; }

    public IReadOnlyList<RobustCapacityRow>? Robust { get; init; }

    public CoverScoreReport? Cover { get; init; }
}

internal sealed record AnalyzeReport(string Input, int Width, int Height, IReadOnlyList<ChannelLsbStats> Channels, string? BitPlaneImage);

internal sealed record KeyReport(string? Output, string Recipient);

internal sealed record AuditReport(string Input, long Tested, bool Found, string? Password, bool Cancelled);

internal sealed record ErrorReport(string Error, int ExitCode);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(IReadOnlyList<HideReport>))]
[JsonSerializable(typeof(IReadOnlyList<RevealReport>))]
[JsonSerializable(typeof(IReadOnlyList<CapacityReport>))]
[JsonSerializable(typeof(IReadOnlyList<AnalyzeReport>))]
[JsonSerializable(typeof(IReadOnlyList<KeyReport>))]
[JsonSerializable(typeof(IReadOnlyList<AuditReport>))]
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
