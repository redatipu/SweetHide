using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

namespace Magi.Core.Payloads;

/// <summary>
/// Body format (encrypted as a whole by the engine):
/// <code>
/// u8  compression   0 = none, 1 = Brotli
/// u32 rawLength     length of the entry list once decompressed (LE)
/// ... entry list, compressed or not:
///     u16 count
///     count × { u8 kind, u16 nameLength, name (UTF-8), u32 contentLength, content }
/// </code>
/// </summary>
internal static class PayloadSerializer
{
    public const int MaxEntries = 1024;
    public const int MaxNameBytes = 1024;
    public const int MaxRawBytes = 256 * 1024 * 1024;

    private const byte CompressionNone = 0;
    private const byte CompressionBrotli = 1;
    private const int PrefixSize = 1 + 4;
    private const int BrotliQuality = 9;
    private const int BrotliWindow = 22;

    public static byte[] Serialize(Payload payload)
    {
        var raw = SerializeEntries(payload);

        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        var useBrotli = BrotliEncoder.TryCompress(raw, compressed, out var compressedLength, BrotliQuality, BrotliWindow)
            && compressedLength < raw.Length;

        var body = useBrotli ? compressed.AsSpan(0, compressedLength) : raw;
        var result = new byte[PrefixSize + body.Length];
        result[0] = useBrotli ? CompressionBrotli : CompressionNone;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), (uint)raw.Length);
        body.CopyTo(result.AsSpan(PrefixSize));
        return result;
    }

    /// <exception cref="InvalidDataException">The body is malformed.</exception>
    public static Payload Deserialize(ReadOnlySpan<byte> body)
    {
        if (body.Length < PrefixSize)
        {
            throw new InvalidDataException("Payload is truncated.");
        }

        var compression = body[0];
        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        if (rawLength > MaxRawBytes)
        {
            throw new InvalidDataException("Payload declares an implausible size.");
        }

        var data = body[PrefixSize..];
        byte[] raw;
        switch (compression)
        {
            case CompressionNone:
                if (data.Length != rawLength)
                {
                    throw new InvalidDataException("Payload length mismatch.");
                }

                raw = data.ToArray();
                break;
            case CompressionBrotli:
                // Exact-size buffer: a stream that would expand past rawLength fails instead of growing (no bombs).
                raw = new byte[rawLength];
                if (!BrotliDecoder.TryDecompress(data, raw, out var written) || written != rawLength)
                {
                    throw new InvalidDataException("Payload failed to decompress.");
                }

                break;
            default:
                throw new InvalidDataException($"Unknown compression method {compression}.");
        }

        return DeserializeEntries(raw);
    }

    private static byte[] SerializeEntries(Payload payload)
    {
        long size = sizeof(ushort);
        var names = new byte[payload.Entries.Count][];
        for (var i = 0; i < payload.Entries.Count; i++)
        {
            var entry = payload.Entries[i];
            names[i] = Encoding.UTF8.GetBytes(entry.Name);
            if (names[i].Length > MaxNameBytes)
            {
                throw new ArgumentException($"File name is longer than {MaxNameBytes} bytes: {entry.Name}");
            }

            size += 1 + sizeof(ushort) + names[i].Length + sizeof(uint) + entry.Length;
        }

        if (size > MaxRawBytes)
        {
            throw new ArgumentException($"Payload is larger than {MaxRawBytes / (1024 * 1024)} MiB.");
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)payload.Entries.Count);
        span = span[sizeof(ushort)..];
        for (var i = 0; i < payload.Entries.Count; i++)
        {
            var entry = payload.Entries[i];
            span[0] = (byte)entry.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(span[1..], (ushort)names[i].Length);
            span = span[(1 + sizeof(ushort))..];
            names[i].CopyTo(span);
            span = span[names[i].Length..];
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)entry.Length);
            span = span[sizeof(uint)..];
            entry.Content.Span.CopyTo(span);
            span = span[entry.Length..];
        }

        return buffer;
    }

    private static Payload DeserializeEntries(ReadOnlySpan<byte> raw)
    {
        var reader = new SpanReader(raw);
        var count = reader.ReadUInt16();
        if (count is 0 or > MaxEntries)
        {
            throw new InvalidDataException("Payload has an invalid entry count.");
        }

        var entries = new List<PayloadEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = (PayloadEntryKind)reader.ReadByte();
            if (kind is not (PayloadEntryKind.Text or PayloadEntryKind.File))
            {
                throw new InvalidDataException($"Unknown entry kind {(byte)kind}.");
            }

            var nameLength = reader.ReadUInt16();
            if (nameLength > MaxNameBytes)
            {
                throw new InvalidDataException("Entry name is too long.");
            }

            string name;
            try
            {
                name = new UTF8Encoding(false, true).GetString(reader.ReadBytes(nameLength));
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Entry name is not valid UTF-8.", ex);
            }

            var contentLength = reader.ReadUInt32();
            var content = reader.ReadBytes(checked((int)contentLength)).ToArray();

            if (kind == PayloadEntryKind.File && name.Length == 0)
            {
                throw new InvalidDataException("File entry has no name.");
            }

            if (kind == PayloadEntryKind.Text && !Utf8.IsValid(content))
            {
                throw new InvalidDataException("Text entry is not valid UTF-8.");
            }

            entries.Add(new PayloadEntry(kind, kind == PayloadEntryKind.Text ? string.Empty : name, content));
        }

        if (!reader.AtEnd)
        {
            throw new InvalidDataException("Unexpected trailing data in payload.");
        }

        return new Payload(entries);
    }

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public readonly bool AtEnd => _data.IsEmpty;

        public byte ReadByte() => ReadBytes(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort)));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > _data.Length)
            {
                throw new InvalidDataException("Payload is truncated.");
            }

            var result = _data[..count];
            _data = _data[count..];
            return result;
        }
    }
}
