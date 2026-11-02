using System.Text.Json.Serialization;

using SweetHide.Forensics.Audio;
using SweetHide.Forensics.Barcodes;
using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Lsb;
using SweetHide.Forensics.Metadata;
using SweetHide.Forensics.Steganalysis;
using SweetHide.Forensics.Text;

namespace SweetHide.Forensics;

/// <summary>
/// Source-generated (trim- and AOT-friendly) JSON for every result type a CLI might print: camelCase names,
/// enums as strings, nulls omitted, NaN written as "NaN". Payload bytes are left out (see <see cref="Finding.Payload"/>).
/// </summary>
/// <example><c>JsonSerializer.Serialize(report, ForensicsJsonContext.Default.StegoFinderReport)</c></example>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(StegoFinderReport))]
[JsonSerializable(typeof(ContainerReport))]
[JsonSerializable(typeof(IReadOnlyList<MetadataField>))]
[JsonSerializable(typeof(IReadOnlyList<LsbScanResult>))]
[JsonSerializable(typeof(IReadOnlyList<ExtractedString>))]
[JsonSerializable(typeof(IReadOnlyList<DecodeResult>))]
[JsonSerializable(typeof(IReadOnlyList<BarcodeResult>))]
[JsonSerializable(typeof(SteganalysisReport))]
[JsonSerializable(typeof(IReadOnlyList<WavLsbScanResult>))]
public sealed partial class ForensicsJsonContext : JsonSerializerContext;
