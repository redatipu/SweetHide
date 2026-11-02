using System.Security.Cryptography;

using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class FeistelPermutationTests
{
    private static readonly byte[] Key = SHA512.HashData("test-key"u8);
    private static readonly long[] KnownAnswer = [520305, 844675, 384810, 405492, 796354];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(4097)]
    [InlineData(65_537)]
    public void Map_is_a_bijection(int size)
    {
        var permutation = new FeistelPermutation(size, Key);
        var seen = new bool[size];
        for (var i = 0; i < size; i++)
        {
            var mapped = permutation.Map(i);
            Assert.InRange(mapped, 0, size - 1);
            Assert.False(seen[mapped], $"{mapped} produced twice");
            seen[mapped] = true;
        }
    }

    [Fact]
    public void Same_key_gives_same_order_and_different_key_gives_different_order()
    {
        var a = new FeistelPermutation(10_000, Key);
        var b = new FeistelPermutation(10_000, Key);
        var c = new FeistelPermutation(10_000, SHA512.HashData("other"u8));

        var orderA = Enumerable.Range(0, 100).Select(i => a.Map(i)).ToArray();
        Assert.Equal(orderA, Enumerable.Range(0, 100).Select(i => b.Map(i)));
        Assert.NotEqual(orderA, Enumerable.Range(0, 100).Select(i => c.Map(i)));
    }

    [Fact]
    public void Known_answer_is_stable_across_platforms()
    {
        // Pins the v1 format: if this changes, images made by older builds can no longer be revealed.
        var permutation = new FeistelPermutation(1_000_000, Key);
        var first = Enumerable.Range(0, 5).Select(i => permutation.Map(i)).ToArray();
        Assert.Equal(KnownAnswer, first);
    }

    [Fact]
    public void Spreads_early_indices_across_the_whole_range()
    {
        const int size = 1_000_000;
        var permutation = new FeistelPermutation(size, Key);
        var buckets = new int[10];
        for (var i = 0; i < 10_000; i++)
        {
            buckets[permutation.Map(i) * 10 / size]++;
        }

        Assert.All(buckets, count => Assert.InRange(count, 850, 1150));
    }
}
