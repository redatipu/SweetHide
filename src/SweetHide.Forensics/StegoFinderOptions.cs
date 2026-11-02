using SweetHide.Forensics.Audio;
using SweetHide.Forensics.Barcodes;
using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Lsb;
using SweetHide.Forensics.Metadata;
using SweetHide.Forensics.Text;

namespace SweetHide.Forensics;

/// <summary>What <see cref="StegoFinder"/> runs and its limits. Every check is on by default.</summary>
public sealed record StegoFinderOptions
{
    /// <summary>Larger inputs are refused (reported as an error, nothing else runs).</summary>
    public long MaxFileBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Payloads attached to findings are cut to this size.</summary>
    public int MaxPayloadBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// All payloads in one report together stay under this. Once it is spent, findings are still reported but carry
    /// no bytes, so a file stuffed with hundreds of embedded signatures cannot make the report huge.
    /// </summary>
    public long MaxTotalPayloadBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Findings below this confidence are dropped from the report.</summary>
    public double MinConfidence { get; init; }

    public int MaxFindings { get; init; } = 200;

    public bool InspectContainer { get; init; } = true;

    public bool ReadMetadata { get; init; } = true;

    public bool SearchStrings { get; init; } = true;

    public bool DecodeText { get; init; } = true;

    public bool ScanLsb { get; init; } = true;

    public bool RunSteganalysis { get; init; } = true;

    public bool ScanBarcodes { get; init; } = true;

    public bool InspectAudio { get; init; } = true;

    /// <summary>Attach a rendered spectrogram PNG to WAV reports.</summary>
    public bool RenderSpectrogram { get; init; } = true;

    public ContainerInspectorOptions Container { get; init; } = ContainerInspectorOptions.Default;

    public MetadataOptions Metadata { get; init; } = MetadataOptions.Default;

    /// <summary>Strings are searched for flag-shaped and encoded text; six characters is the shortest useful flag.</summary>
    public StringsOptions Strings { get; init; } = new() { MinLength = 6 };

    public DecoderOptions Decoders { get; init; } = DecoderOptions.Default;

    public LsbScanOptions Lsb { get; init; } = LsbScanOptions.Default;

    public BarcodeScanOptions Barcodes { get; init; } = BarcodeScanOptions.Default;

    public WavLsbScanOptions AudioLsb { get; init; } = WavLsbScanOptions.Default;

    public SpectrogramOptions Spectrogram { get; init; } = SpectrogramOptions.Default;

    public static StegoFinderOptions Default { get; } = new();
}
