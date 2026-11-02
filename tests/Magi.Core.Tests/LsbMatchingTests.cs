using Magi.Core.Stego;

namespace Magi.Core.Tests;

public class LsbMatchingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Result_has_target_low_bits_and_minimal_change(int bits)
    {
        var modulus = 1 << bits;
        for (var value = 0; value < 256; value++)
        {
            for (var target = 0; target < modulus; target++)
            {
                foreach (var coin in new[] { false, true })
                {
                    var result = LsbMatching.Embed((byte)value, target, bits, coin);
                    Assert.Equal(target, result & (modulus - 1));

                    // No closer in-range value has the same low bits.
                    var distance = Math.Abs(result - value);
                    for (var candidate = 0; candidate < 256; candidate++)
                    {
                        if ((candidate & (modulus - 1)) == target)
                        {
                            Assert.True(Math.Abs(candidate - value) >= distance);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void One_bit_mismatch_moves_by_exactly_one_in_either_direction()
    {
        Assert.Equal(101, LsbMatching.Embed(100, 1, 1, coin: true));
        Assert.Equal(99, LsbMatching.Embed(100, 1, 1, coin: false));
        Assert.Equal(1, LsbMatching.Embed(0, 1, 1, coin: false));     // clamped at 0
        Assert.Equal(254, LsbMatching.Embed(255, 0, 1, coin: true));  // clamped at 255
        Assert.Equal(100, LsbMatching.Embed(100, 0, 1, coin: true));  // already matches
    }
}
