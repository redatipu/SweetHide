using FsCheck;
using FsCheck.Xunit;

using SweetHide.Core.Jpeg;

namespace SweetHide.Core.Tests.Jpeg;

public class JpegCoefficientSlotsTests
{
    [Fact]
    public void Slots_are_the_ac_coefficients_with_magnitude_at_least_two()
    {
        var coefficients = JpegEncoder.Quantize(JpegTestImages.Textured(90, 50), 85);
        long expected = 0;
        foreach (var component in coefficients.Components)
        {
            for (var by = 0; by < component.EncodedBlocksHigh; by++)
            {
                for (var bx = 0; bx < component.EncodedBlocksWide; bx++)
                {
                    var block = component.Block(bx, by);
                    for (var k = 1; k < 64; k++)
                    {
                        expected += Math.Abs((int)block[k]) >= 2 ? 1 : 0;
                    }
                }
            }
        }

        var slots = new JpegCoefficientSlots(coefficients);

        Assert.Equal(expected, slots.SlotCount);
        Assert.True(slots.SlotCount > 1000);
        Assert.Equal(1, slots.BitsPerSlot);
    }

    [Property(MaxTest = 30)]
    public void Embedding_keeps_the_slot_set_and_survives_a_file_roundtrip(PositiveInt seed, bool yuv420)
    {
        var random = new Random(seed.Get);
        var cover = JpegEncoder.Quantize(JpegTestImages.Textured(48, 40, seed.Get), random.Next(50, 96), yuv420 ? JpegSubsampling.Yuv420 : JpegSubsampling.Yuv444);
        var stego = cover.Clone();
        var slots = new JpegCoefficientSlots(stego);
        var bits = new int[slots.SlotCount];
        for (long s = 0; s < slots.SlotCount; s++)
        {
            bits[s] = random.Next(2);
            slots.Write(s, bits[s], random.Next(2) == 1);
        }

        // Rebuild the view from a written and re-read file, as a receiver would.
        var received = JpegDecoder.Read(JpegTestImages.Write(stego, new JpegWriteOptions { OptimizeHuffmanTables = true }));
        var again = new JpegCoefficientSlots(received);

        Assert.Equal(slots.SlotCount, again.SlotCount);
        for (long s = 0; s < again.SlotCount; s++)
        {
            Assert.Equal(bits[s], again.Read(s));
        }

        for (var c = 0; c < cover.Components.Count; c++)
        {
            var before = cover.Components[c].Coefficients;
            var after = received.Components[c].Coefficients;
            for (var i = 0; i < before.Length; i++)
            {
                Assert.InRange(after[i] - before[i], -1, 1);
                Assert.Equal(JpegCoefficientSlots.IsUsable(before[i]), JpegCoefficientSlots.IsUsable(after[i]));
                if (i % 64 == 0)
                {
                    Assert.Equal(before[i], after[i]); // DC never changes
                }
            }
        }
    }

    [Theory]
    [InlineData(2, 1, true, 3)]
    [InlineData(2, 1, false, 3)]
    [InlineData(-2, 1, false, -3)]
    [InlineData(3, 0, true, 4)]
    [InlineData(3, 0, false, 2)]
    [InlineData(-3, 0, false, -2)]
    [InlineData(1023, 0, true, 1022)]
    [InlineData(-1023, 0, true, -1022)]
    [InlineData(-5, 1, true, -5)]
    public void Writes_move_by_one_and_never_below_two(short value, int target, bool coin, short expected)
    {
        var coefficients = JpegEncoder.Quantize(TestImages.Solid(8, 8, 128, 128, 128), 90, JpegSubsampling.Yuv444);
        coefficients.Components[0].Block(0, 0)[1] = value;
        var slots = new JpegCoefficientSlots(coefficients);
        Assert.Equal(1, slots.SlotCount);

        slots.Write(0, target, coin);

        Assert.Equal(expected, coefficients.Components[0].Block(0, 0)[1]);
        Assert.Equal(target, slots.Read(0));
    }

    [Fact]
    public void Validates_arguments()
    {
        var slots = new JpegCoefficientSlots(JpegEncoder.Quantize(JpegTestImages.Textured(16, 16), 90));

        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Read(slots.SlotCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Read(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Read(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Write(0, 2, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Write(0, 1, 2, false));
    }

    [Fact]
    public void Padding_blocks_outside_the_image_are_never_slots()
    {
        // 4:2:0 at 20x20: chroma is 10x10 => 2x2 encoded blocks, while MCU padding stores 2x2 as well; luma stores 4x4
        // but only 3x3 cover pixels. Busy padding blocks must stay untouched.
        var coefficients = JpegEncoder.Quantize(JpegTestImages.Textured(20, 20), 95);
        var luma = coefficients.Components[0];
        Assert.Equal(4, luma.BlocksWide);
        Assert.Equal(3, luma.EncodedBlocksWide);
        luma.Block(3, 3).Fill(50);

        var slots = new JpegCoefficientSlots(coefficients);
        for (long s = 0; s < slots.SlotCount; s++)
        {
            slots.Write(s, 1 - slots.Read(s), coin: true);
        }

        Assert.All(luma.Block(3, 3).ToArray(), v => Assert.Equal(50, v));
    }
}
