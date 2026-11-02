using FsCheck;
using FsCheck.Xunit;

using SweetHide.Core.Robust;

namespace SweetHide.Core.Tests.Robust;

public class ReedSolomonTests
{
    [Fact]
    public void Matches_the_qr_code_known_answer()
    {
        // "hello world" as a version 1-M QR code (the worked example in "Reed–Solomon codes for coders").
        byte[] data = [0x40, 0xd2, 0x75, 0x47, 0x76, 0x17, 0x32, 0x06, 0x27, 0x26, 0x96, 0xc6, 0xc6, 0x96, 0x70, 0xec];
        var parity = new byte[10];

        new ReedSolomon(10).Encode(data, parity);

        Assert.Equal(new byte[] { 0xbc, 0x2a, 0x90, 0x13, 0x6b, 0xaf, 0xef, 0xfd, 0x4b, 0xe0 }, parity);
    }

    [Property(MaxTest = 300)]
    public void Corrects_any_errors_and_erasures_within_the_bound(PositiveInt seed)
    {
        var random = new Random(seed.Get);
        var parity = random.Next(2, 65);
        var dataLength = random.Next(1, ReedSolomon.MaxCodewordLength - parity + 1);
        var codec = new ReedSolomon(parity);
        var original = Codeword(codec, random, dataLength);

        // Any mix with 2·errors + erasures ≤ parity.
        var erasureCount = random.Next(0, parity + 1);
        var errorCount = random.Next(0, ((parity - erasureCount) / 2) + 1);
        var positions = Enumerable.Range(0, original.Length).OrderBy(_ => random.Next()).Take(erasureCount + errorCount).ToArray();
        var received = (byte[])original.Clone();
        foreach (var p in positions)
        {
            received[p] ^= (byte)random.Next(1, 256);
        }

        var erasures = positions[..erasureCount];
        if (random.Next(2) == 0 && erasureCount > 0)
        {
            received[erasures[0]] = original[erasures[0]]; // An erased symbol may also happen to be right.
        }

        Assert.True(codec.TryDecode(received, erasures, out var corrected));
        Assert.Equal(original, received);
        Assert.True(corrected <= errorCount + erasureCount);
    }

    [Property(MaxTest = 200)]
    public void Too_many_errors_never_crash_and_rarely_miscorrect(PositiveInt seed)
    {
        var random = new Random(seed.Get);
        var parity = random.Next(4, 33);
        var codec = new ReedSolomon(parity);
        var original = Codeword(codec, random, random.Next(1, 100));
        var received = (byte[])original.Clone();
        var errors = Math.Min(original.Length, (parity / 2) + random.Next(1, 10));
        foreach (var p in Enumerable.Range(0, original.Length).OrderBy(_ => random.Next()).Take(errors))
        {
            received[p] ^= (byte)random.Next(1, 256);
        }

        var before = (byte[])received.Clone();
        if (codec.TryDecode(received, [], out _))
        {
            // Beyond the bound the decoder may land on another valid codeword; that is why payloads carry a CRC.
            Assert.NotEqual(before, received);
        }
        else
        {
            Assert.Equal(before, received); // left untouched on failure
        }
    }

    [Fact]
    public void Clean_codewords_need_no_correction()
    {
        var codec = new ReedSolomon(16);
        var word = Codeword(codec, new Random(1), 100);

        Assert.True(codec.TryDecode(word, [], out var corrected));
        Assert.Equal(0, corrected);
    }

    [Fact]
    public void Validates_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(255));
        var codec = new ReedSolomon(10);
        Assert.Throws<ArgumentException>(() => codec.Encode(new byte[246], new byte[10]));
        Assert.Throws<ArgumentException>(() => codec.Encode(new byte[10], new byte[9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.TryDecode(new byte[20], [20], out _));
        Assert.False(codec.TryDecode(new byte[20], Enumerable.Range(0, 11).ToArray(), out _));
    }

    private static byte[] Codeword(ReedSolomon codec, Random random, int dataLength)
    {
        var word = new byte[dataLength + codec.ParitySymbols];
        random.NextBytes(word.AsSpan(0, dataLength));
        codec.Encode(word.AsSpan(0, dataLength), word.AsSpan(dataLength));
        return word;
    }
}
