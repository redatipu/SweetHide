using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Forensics.Audio;
using SweetHide.Forensics.Barcodes;
using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Lsb;
using SweetHide.Forensics.Metadata;
using SweetHide.Forensics.Steganalysis;
using SweetHide.Forensics.Text;

using static System.FormattableString;

namespace SweetHide.Forensics;

/// <summary>
/// One call that runs every relevant check on a file and returns a ranked list of findings. Read-only: the input is
/// never modified and nothing is written to disk.
/// </summary>
/// <remarks>
/// Each check runs on its own: if one fails (a corrupt image that will not decode, say), its error is recorded in
/// <see cref="StegoFinderReport.Errors"/> and the others still report. Only cancellation stops the whole analysis.
/// </remarks>
public sealed class StegoFinder
{
    private const int MaxLsbFindings = 8;
    private const int MaxStructureFindings = 24;
    private const int MaxShapeOnlyStrings = 10;
    private const int DetailChars = 500;
    private const double ReportThreshold = 0.5;

    private static readonly HashSet<FileType> ImageTypes = [FileType.Png, FileType.Jpeg, FileType.Gif, FileType.WebP, FileType.Bmp];

    private static readonly HashSet<string> RoutineMetadata = new(StringComparer.Ordinal)
    {
        "Make", "Model", "Software", "DateTime", "DateTimeOriginal", "DateTimeDigitized", "OffsetTime", "OffsetTimeOriginal",
        "OffsetTimeDigitized", "LensMake", "LensModel", "xmp:CreateDate", "xmp:ModifyDate", "xmp:MetadataDate", "xmp:CreatorTool",
    };

    private readonly IImageCodec _codec;

    public StegoFinder(StegoFinderOptions? options = null, IImageCodec? codec = null)
    {
        Options = options ?? StegoFinderOptions.Default;
        _codec = codec ?? new SkiaImageCodec();
    }

    public StegoFinderOptions Options { get; }

    /// <summary>Reads a file (refusing anything over <see cref="StegoFinderOptions.MaxFileBytes"/>) and analyses it.</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    /// <exception cref="OperationCanceledException">The analysis was cancelled.</exception>
    public StegoFinderReport AnalyzeFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var length = new FileInfo(path).Length;
        if (length > Options.MaxFileBytes)
        {
            return TooLarge(Path.GetFileName(path), length);
        }

        return Analyze(File.ReadAllBytes(path), Path.GetFileName(path), cancellationToken);
    }

    /// <summary>Runs every enabled check on the file's bytes.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="fileName">Optional name, only echoed in the report.</param>
    /// <param name="cancellationToken">Stops the analysis between (and inside) checks.</param>
    /// <exception cref="OperationCanceledException">The analysis was cancelled.</exception>
    public StegoFinderReport Analyze(ReadOnlyMemory<byte> file, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (file.Length > Options.MaxFileBytes)
        {
            return TooLarge(fileName, file.Length);
        }

        var watch = Stopwatch.StartNew();
        var session = new Session(Options, file, cancellationToken);
        var format = FileSignatures.Identify(file.Span);

        if (Options.InspectContainer)
        {
            session.Run("Container", () => session.InspectContainer());
        }

        if (Options.ReadMetadata)
        {
            session.Run("Metadata", () => session.ReadMetadata());
        }

        if (Options.SearchStrings)
        {
            session.Run("Strings", () => session.SearchStrings());
        }

        PixelBuffer? image = null;
        if (format is null || ImageTypes.Contains(format.Type))
        {
            try
            {
                using var stream = AsStream(file);
                image = _codec.Decode(stream, applyOrientation: false);
            }
            catch (InvalidImageException ex) when (format is not null)
            {
                session.Errors.Add($"Image: {ex.Message}");
            }
            catch (InvalidImageException)
            {
                // Not an image and not a recognised format: the byte-level checks above are all there is.
            }
        }

        if (image is not null)
        {
            if (Options.ScanLsb)
            {
                session.Run("LSB scan", () => session.ScanLsb(image));
            }

            if (Options.RunSteganalysis)
            {
                session.Run("Steganalysis", () => session.RunSteganalysis(image));
            }

            if (Options.ScanBarcodes)
            {
                session.Run("Barcodes", () => session.ScanBarcodes(image));
            }
        }

        if (Options.InspectAudio && format?.Type == FileType.Wav)
        {
            session.Run("Audio", () => session.InspectAudio(_codec));
        }

        var findings = session.Findings
            .Where(f => f.Confidence >= Options.MinConfidence)
            .OrderByDescending(f => f.Confidence)
            .Take(Options.MaxFindings)
            .ToList();
        return new StegoFinderReport(fileName, file.Length, format?.Name ?? "unknown", image?.Width, image?.Height, findings, session.Errors, watch.ElapsedMilliseconds);
    }

    /// <summary>A read-only stream over the bytes, without copying them when they live in an array.</summary>
    private static MemoryStream AsStream(ReadOnlyMemory<byte> file) =>
        MemoryMarshal.TryGetArray(file, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(file.ToArray(), writable: false);

    private StegoFinderReport TooLarge(string? fileName, long length) =>
        new(fileName, length, "unknown", null, null, [], [Invariant($"The file is {ByteSize.Format(length)}; the limit is {ByteSize.Format(Options.MaxFileBytes)}.")], 0);

    /// <summary>State of one analysis: the findings so far, errors, and what has already been reported.</summary>
    private sealed class Session(StegoFinderOptions options, ReadOnlyMemory<byte> file, CancellationToken cancellationToken)
    {
        private const int DecodeBudget = 64;

        private readonly HashSet<string> _decodedInputs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _decodedOutputs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
        private List<StructureItem>? _structure;
        private int _decodes;

        public List<Finding> Findings { get; } = [];

        public List<string> Errors { get; } = [];

        /// <summary>Runs one check; a failure becomes an error line instead of ending the analysis.</summary>
        public void Run(string check, Action action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Errors.Add($"{check}: {ex.Message}");
            }
        }

        public void InspectContainer()
        {
            var report = ContainerInspector.Inspect(file, options.Container, cancellationToken);
            _structure = [.. report.Structure];
            if (report.CorrectedDimensions is { } d)
            {
                Add(new Finding(
                    FindingKind.StructureAnomaly,
                    "Image dimensions were edited",
                    Invariant($"The IHDR CRC belongs to {d.Width}x{d.Height}, but the header says {d.StoredWidth}x{d.StoredHeight}. Restore the real size to see the hidden part of the image."),
                    0.9)
                {
                    Source = "IHDR",
                    Offset = 8,
                });
            }

            var multiPicture = report.Warnings.Any(w => w.Contains("Multi-Picture", StringComparison.Ordinal));
            if (report.Trailing is { } trailing)
            {
                AddTrailing(trailing, multiPicture);
            }

            foreach (var embedded in report.Embedded.Where(e => !e.IsWholeFile && e.Offset != report.Trailing?.Offset))
            {
                var thumbnail = embedded.Type.Type == FileType.Jpeg && embedded.Location?.StartsWith("APP1", StringComparison.Ordinal) == true;
                var size = embedded.Length is { } l ? ByteSize.Format(l) : "unknown size";
                Add(new Finding(
                    FindingKind.EmbeddedFile,
                    thumbnail ? "EXIF thumbnail" : $"{embedded.Type.Name} inside the file",
                    Invariant($"{embedded.Type.Name} at 0x{embedded.Offset:X} ({size}){(embedded.Location is null ? string.Empty : $", in {embedded.Location}")}."),
                    thumbnail ? 0.2 : 0.75)
                {
                    Source = embedded.Location,
                    Offset = embedded.Offset,
                    Payload = Cap(Carver.Carve(file, embedded).Span),
                    SuggestedFileName = Carver.SuggestFileName(embedded.Offset, embedded.Type),
                });
            }

            foreach (var item in report.Structure.Where(s => (s.Traits & (StructureTraits.Suspicious | StructureTraits.CrcMismatch)) != 0).Take(MaxStructureFindings))
            {
                AddStructure(item);
            }

            foreach (var warning in report.Warnings)
            {
                Add(new Finding(FindingKind.StructureAnomaly, "Structure warning", warning, warning.Contains("Multi-Picture", StringComparison.Ordinal) ? 0.05 : 0.25));
            }
        }

        public void ReadMetadata()
        {
            foreach (var field in MetadataReader.Read(file, options.Metadata, cancellationToken))
            {
                if (field.Name == "XMP packet" && !field.IsUnusual)
                {
                    continue; // its properties are reported one by one
                }

                var confidence = field.IsUnusual ? 0.6 : RoutineMetadata.Contains(field.Name) ? 0.05 : field.Name == "Position" ? 0.4 : 0.2;
                confidence = Math.Max(confidence, FlagConfidence(field.Value));
                RememberFlags(field.Value);
                var detail = Shorten(field.Value) + (field.Note is null ? string.Empty : $" ({field.Note})");
                Add(new Finding(FindingKind.Metadata, $"{field.Source}: {field.Name}", detail, confidence)
                {
                    Source = $"{field.Source} {field.Name}",
                    Offset = field.Offset,
                    Payload = field.Value.Length > DetailChars ? Cap(Encoding.UTF8.GetBytes(field.Value)) : null,
                    SuggestedFileName = field.Value.Length > DetailChars ? SafeFileName.Sanitize($"metadata_{field.Name}.txt") : null,
                });
                TryDecode(field.Value, $"{field.Source} {field.Name}");
            }
        }

        public void SearchStrings()
        {
            var noise = CompressedRanges();
            var shapeOnly = 0;
            foreach (var found in StringsExtractor.Extract(file, options.Strings, cancellationToken))
            {
                // Printable runs inside compressed pixel data are noise; only a real flag format is worth reporting there.
                var inNoise = Contains(noise, found.Offset);
                foreach (var flag in FlagPattern.FindAll(found.Value))
                {
                    var likely = FlagPattern.IsLikelyFlag(flag);
                    if (_flags.Contains(flag) || (!likely && (inNoise || shapeOnly++ >= MaxShapeOnlyStrings)))
                    {
                        continue;
                    }

                    _flags.Add(flag);
                    Add(new Finding(FindingKind.InterestingString, likely ? "Flag in the file's bytes" : "Flag-shaped string", flag, likely ? 0.95 : 0.3)
                    {
                        Source = Invariant($"{found.Encoding} string at 0x{found.Offset:X}"),
                        Offset = found.Offset,
                    });
                }

                if (!inNoise && found.Value.Length >= 16 && LooksEncoded(found.Value))
                {
                    TryDecode(found.Value, Invariant($"string at 0x{found.Offset:X}"));
                }
            }
        }

        public void ScanLsb(PixelBuffer image)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in LsbScanner.Scan(image, options.Lsb, cancellationToken).Where(r => r.Content.Score >= ReportThreshold).Take(MaxLsbFindings))
            {
                var payload = LsbScanner.ExtractPayload(image, result, options.MaxPayloadBytes, cancellationToken);
                if (seen.Add(Convert.ToHexString(SHA256.HashData(payload))))
                {
                    AddStreamPayload(FindingKind.LsbPayload, "LSBs", result.Config.Name, result.Content, result.StreamCapacity, payload);
                }
            }
        }

        public void RunSteganalysis(PixelBuffer image)
        {
            var report = Steganalyzer.Analyze(image, cancellationToken);
            var map = LsbRandomness.Map(image, renderHeatmap: false, cancellationToken: cancellationToken);
            var rate = report.MaxEstimatedRate;
            var chi = report.Channels.Select(c => c.ChiSquareProbability).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            var measurable = rate >= 0.15;
            var chiPositive = chi > 0.95;

            // Chi-square alone misfires on images with flat histograms (gradients, noise), so it only adds weight
            // when RS or SPA also measure a payload; on its own it is a note.
            var confidence = measurable ? Math.Min(0.85, 0.3 + rate) : 0.05;
            confidence = Math.Max(confidence, chiPositive ? (measurable ? 0.8 : 0.35) : 0);
            confidence = Math.Max(confidence, map.SuspiciousBlockRatio > 0.05 ? 0.5 : 0);

            var detail = new StringBuilder();
            foreach (var c in report.Channels)
            {
                detail.Append(Invariant($"{c.Channel}: LSB=1 ratio {c.OnesRatio:0.000}, chi-square p {c.ChiSquareProbability:0.000}, RS {c.RsRate:0.00}, SPA {c.SpaRate:0.00}. "));
            }

            detail.Append(Invariant($"LSBs are random while bit 1 is not in {map.SuspiciousBlockRatio:P0} of {map.BlockSize}x{map.BlockSize} blocks."));
            var title = measurable
                ? Invariant($"About {rate:P0} of the samples look rewritten (LSB replacement)")
                : chiPositive ? "Chi-square test is positive but RS and SPA measure nothing" : "No strong statistical sign of LSB replacement";
            Add(new Finding(FindingKind.Steganalysis, title, detail.ToString().Trim(), confidence) { Source = "chi-square, RS, SPA" });
        }

        public void ScanBarcodes(PixelBuffer image)
        {
            foreach (var barcode in BarcodeScanner.Scan(image, options.Barcodes, cancellationToken))
            {
                var inImage = barcode.Source == "image";
                var confidence = Math.Max(inImage ? 0.6 : 0.9, FlagConfidence(barcode.Text));
                RememberFlags(barcode.Text);
                Add(new Finding(FindingKind.Barcode, inImage ? $"{barcode.Format} in the image" : $"{barcode.Format} hidden in {barcode.Source}", Shorten(barcode.Text), confidence)
                {
                    Source = barcode.Source,
                    Payload = Encoding.UTF8.GetBytes(barcode.Text),
                    SuggestedFileName = "barcode.txt",
                });
                TryDecode(barcode.Text, barcode.Source);
            }
        }

        public void InspectAudio(IImageCodec codec)
        {
            if (!WavReader.TryRead(file, out var audio, out var error))
            {
                Errors.Add($"Audio: {error}");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in WavInspector.ScanLsb(audio, options.AudioLsb, cancellationToken).Where(r => r.Content.Score >= ReportThreshold).Take(MaxLsbFindings))
            {
                var payload = WavInspector.ExtractPayload(audio, result, options.MaxPayloadBytes, cancellationToken);
                if (seen.Add(Convert.ToHexString(SHA256.HashData(payload))))
                {
                    AddStreamPayload(FindingKind.AudioLsb, "sample LSBs", result.Config.Name, result.Content, result.StreamCapacity, payload);
                }
            }

            if (options.RenderSpectrogram)
            {
                var spectrogram = Spectrogram.Render(audio, options.Spectrogram, cancellationToken);
                using var png = new MemoryStream();
                codec.EncodePng(spectrogram, png);
                Add(new Finding(
                    FindingKind.Spectrogram,
                    "Spectrogram",
                    Invariant($"{spectrogram.Width}x{spectrogram.Height} rendering of {audio.Duration.TotalSeconds:0.0} s of audio. Look for letters or shapes drawn into the frequencies."),
                    0.3)
                {
                    Payload = png.ToArray(),
                    SuggestedFileName = "spectrogram.png",
                });
            }
        }

        /// <summary>Compressed pixel or sample data (IDAT, JPEG scans, GIF frames, WebP bitstreams, WAV samples, BMP pixels), sorted.</summary>
        private List<(long Start, long End)> CompressedRanges()
        {
            var structure = _structure ?? ContainerInspector.Walk(file.Span, FileSignatures.Identify(file.Span)?.Type ?? FileType.Unknown, options.Container).Items;
            return [.. structure
                .Where(s => s.Type is "IDAT" or "fdAT" or "Scan data" or "Image" or "VP8 " or "VP8L" or "ALPH" or "ANMF" or "data" or "Pixel array")
                .Select(s => (s.DataOffset, s.DataOffset + s.DataLength))
                .OrderBy(r => r.Item1)];
        }

        private static bool Contains(List<(long Start, long End)> ranges, long offset)
        {
            int lo = 0, hi = ranges.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                if (offset < ranges[mid].Start)
                {
                    hi = mid - 1;
                }
                else if (offset >= ranges[mid].End)
                {
                    lo = mid + 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        private void AddTrailing(TrailingData trailing, bool multiPicture)
        {
            var bytes = file.Span.Slice((int)trailing.Offset, (int)trailing.Length);
            var content = trailing.Content;
            var padding = bytes.Length <= 16 && bytes.IndexOfAnyExcept("\0\r\n \t"u8) < 0;
            var isText = content.Kind is ContentKind.Text or ContentKind.TerminatedText or ContentKind.LengthPrefixedText;
            var confidence = padding ? 0.1
                : multiPicture && content.FileType == FileType.Jpeg ? 0.3
                : content.FileType != FileType.Unknown ? 0.9
                : isText ? 0.85
                : 0.7;
            var text = isText ? Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, options.MaxPayloadBytes)]) : null;
            confidence = Math.Max(confidence, FlagConfidence(text));
            RememberFlags(text);

            var extension = content.FileType != FileType.Unknown ? FileSignatures.MatchFor(content.FileType).Extension : isText ? ".txt" : ".bin";
            Add(new Finding(
                FindingKind.TrailingData,
                content.DetectedType is { } type ? $"{type} appended after the end of the image" : Invariant($"{ByteSize.Format(trailing.Length)} after the end of the file's data"),
                Invariant($"{trailing.Length} bytes at 0x{trailing.Offset:X}: {content.Preview}"),
                confidence)
            {
                Source = "trailing data",
                Offset = trailing.Offset,
                Payload = Cap(bytes),
                SuggestedFileName = Invariant($"trailing_0x{trailing.Offset:X}{extension}"),
            });

            if (text is not null)
            {
                TryDecode(text, "trailing data");
            }
        }

        private void AddStructure(StructureItem item)
        {
            var traits = item.Traits;
            var (title, confidence) =
                (traits & StructureTraits.CrcMismatch) != 0 ? ($"CRC mismatch in {item.Type}", 0.6)
                : (traits & StructureTraits.Unknown) != 0 ? ($"Unknown {item.Type.Trim()} block", 0.55)
                : (traits & StructureTraits.Truncated) != 0 ? ($"Truncated {item.Type}", 0.25)
                : ($"Suspicious {item.Type}", 0.6);
            var withData = (traits & (StructureTraits.Unknown | StructureTraits.Suspicious)) != 0 && item.DataLength > 0;
            Add(new Finding(FindingKind.StructureAnomaly, title, Invariant($"{item.Type} at 0x{item.Offset:X}: {item.Description}"), confidence)
            {
                Source = item.Type,
                Offset = item.Offset,
                Payload = withData ? Cap(file.Span.Slice((int)item.DataOffset, (int)item.DataLength)) : null,
                SuggestedFileName = withData ? SafeFileName.Sanitize(Invariant($"{item.Type.Trim()}_0x{item.Offset:X}.bin")) : null,
            });
        }

        private void AddStreamPayload(FindingKind kind, string where, string config, ContentAnalysis content, long capacity, byte[] payload)
        {
            var isFile = content.Kind is ContentKind.File or ContentKind.LengthPrefixedFile;
            var text = isFile ? null : Encoding.UTF8.GetString(payload);
            var title = isFile ? $"{content.DetectedType} hidden in {where} ({config})" : $"Text hidden in {where} ({config})";
            var framing = content.Framing is null ? string.Empty : $" Framing: {content.Framing}.";
            var extension = isFile ? FileSignatures.MatchFor(content.FileType).Extension : ".txt";
            Add(new Finding(kind, title, Invariant($"{(isFile ? content.DetectedType : Shorten(text!))} ({payload.Length} bytes; this configuration holds up to {ByteSize.Format(capacity)}).{framing}"), Math.Max(content.Score, FlagConfidence(text)))
            {
                Source = config,
                Payload = Cap(payload),
                SuggestedFileName = SafeFileName.Sanitize($"{(kind == FindingKind.AudioLsb ? "audio" : "lsb")}_{config.Replace(',', '_')}{extension}"),
            });

            RememberFlags(text);
            if (text is not null)
            {
                TryDecode(text, config);
            }
        }

        /// <summary>Runs the decoder chain on text that might be encoded, once per distinct input, within a budget.</summary>
        private void TryDecode(string text, string origin)
        {
            text = text.Trim();
            if (!options.DecodeText || text.Length < 8 || _decodes >= DecodeBudget || !_decodedInputs.Add(text))
            {
                return;
            }

            _decodes++;
            foreach (var result in DecoderChain.Decode(text, options.Decoders, cancellationToken).Where(r => r.Score >= 0.6).Take(3))
            {
                var output = result.Text ?? result.FileType?.Name ?? string.Empty;
                if (!_decodedOutputs.Add(output))
                {
                    continue;
                }

                RememberFlags(result.Text);
                Add(new Finding(FindingKind.EncodedText, $"Decodes with {result.Chain}", Shorten(output), Math.Max(result.Score * 0.9, FlagConfidence(result.Text)))
                {
                    Source = origin,
                    Payload = result.FileBytes ?? (result.Text is null ? null : Encoding.UTF8.GetBytes(result.Text)),
                    SuggestedFileName = result.FileType is { } type ? $"decoded{type.Extension}" : "decoded.txt",
                });
            }
        }

        private void Add(Finding finding)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Findings.Add(finding);
        }

        private void RememberFlags(string? text)
        {
            foreach (var flag in FlagPattern.FindAll(text))
            {
                _flags.Add(flag);
            }
        }

        private byte[] Cap(ReadOnlySpan<byte> data) => data[..Math.Min(data.Length, options.MaxPayloadBytes)].ToArray();

        private static double FlagConfidence(string? text) =>
            FlagPattern.IsLikelyFlag(text) ? 0.95 : FlagPattern.IsMatch(text) ? 0.7 : 0;

        /// <summary>No spaces and only Base64/Base32/hex characters: worth handing to the decoder chain.</summary>
        private static bool LooksEncoded(string text) =>
            text.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_');

        private static string Shorten(string text) => text.Length <= DetailChars ? text : string.Concat(text.AsSpan(0, DetailChars), "…");
    }
}
