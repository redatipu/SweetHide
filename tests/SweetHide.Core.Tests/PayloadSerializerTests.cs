using System.Buffers.Binary;
using System.Text;

using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class PayloadSerializerTests
{
    [Fact]
    public void Roundtrips_text_and_files()
    {
        var payload = new Payload(
        [
            PayloadEntry.FromText("สวัสดีชาวโลก 👋 hello"),
            PayloadEntry.FromFile("notes.txt", "file body"u8),
            PayloadEntry.FromFile(@"C:\secret\dir\รูป.bin", new byte[] { 0, 1, 2, 255 }),
            PayloadEntry.FromFile("empty.dat", ReadOnlySpan<byte>.Empty),
        ]);

        var back = PayloadSerializer.Deserialize(PayloadSerializer.Serialize(payload));

        Assert.Equal(4, back.Entries.Count);
        Assert.Equal("สวัสดีชาวโลก 👋 hello", back.Entries[0].GetText());
        Assert.Equal("notes.txt", back.Entries[1].Name);
        Assert.Equal("file body"u8.ToArray(), back.Entries[1].Content.ToArray());
        Assert.Equal("รูป.bin", back.Entries[2].Name); // directories are dropped when the entry is created
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, back.Entries[2].Content.ToArray());
        Assert.Equal(0, back.Entries[3].Length);
    }

    [Fact]
    public void Compresses_repetitive_content_and_stores_random_content_raw()
    {
        var repetitive = Payload.FromText(new string('ก', 10_000));
        Assert.True(PayloadSerializer.Serialize(repetitive).Length < 1_000);

        var random = new byte[10_000];
        new Random(5).NextBytes(random);
        var serialized = PayloadSerializer.Serialize(new Payload([PayloadEntry.FromFile("r.bin", random)]));
        Assert.Equal(0, serialized[0]); // CompressionNone
    }

    [Fact]
    public void Rejects_truncated_input()
    {
        var serialized = PayloadSerializer.Serialize(Payload.FromText("hello world"));
        for (var length = 0; length < serialized.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(serialized.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Rejects_a_declared_size_that_does_not_match_the_data()
    {
        var serialized = PayloadSerializer.Serialize(Payload.FromText(new string('x', 5000)));
        Assert.Equal(1, serialized[0]); // Brotli
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(1), 100); // claims less than it expands to
        Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(serialized));

        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(1), uint.MaxValue); // decompression bomb claim
        Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(serialized));
    }

    [Fact]
    public void Rejects_unknown_compression_and_entry_kinds()
    {
        var serialized = PayloadSerializer.Serialize(Payload.FromText("hi"));
        var badCompression = (byte[])serialized.Clone();
        badCompression[0] = 7;
        Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(badCompression));

        var badKind = (byte[])serialized.Clone();
        Assert.Equal(0, badKind[0]); // tiny payloads are stored raw
        badKind[5 + 2] = 9;          // prefix(5) + count(2) → first entry's kind byte
        Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(badKind));
    }

    [Fact]
    public void Rejects_invalid_utf8_text()
    {
        var serialized = PayloadSerializer.Serialize(new Payload([PayloadEntry.FromText("ab")]));
        serialized[^1] = 0xFF;
        Assert.Throws<InvalidDataException>(() => PayloadSerializer.Deserialize(serialized));
    }

    [Fact]
    public void Text_from_entries_uses_strict_utf8()
    {
        Assert.Throws<EncoderFallbackException>(() => PayloadEntry.FromText("\uD800 lone surrogate"));
    }
}
