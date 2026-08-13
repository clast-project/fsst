// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Fsst.Tests;

public class PairCounterTests
{
    private static Dictionary<(int, int), int> Drain(PairCounter counter)
    {
        var result = new Dictionary<(int, int), int>();
        var keys = counter.Keys;
        var counts = counter.Counts;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] < 0) continue;
            result.Add((PairCounter.Pos1(keys[i]), PairCounter.Pos2(keys[i])), counts[i]);
        }
        return result;
    }

    [Fact]
    public void CountsAreExactForRepeatedPairs()
    {
        var counter = new PairCounter();
        for (int i = 0; i < 1000; i++) counter.Increment(7, 9);
        for (int i = 0; i < 3; i++) counter.Increment(9, 7);

        var drained = Drain(counter);
        Assert.Equal(2, counter.Count);
        Assert.Equal(1000, drained[(7, 9)]);
        Assert.Equal(3, drained[(9, 7)]);
    }

    [Fact]
    public void PositionsRoundTripAcrossTheFullCodeSpace()
    {
        // FSST16 codes reach 65,535, and the pair is packed into one long.
        var counter = new PairCounter();
        (int, int)[] edges = [(0, 0), (0, 65535), (65535, 0), (65535, 65535), (1, 65534)];
        foreach (var (a, b) in edges) counter.Increment(a, b);

        var drained = Drain(counter);
        Assert.Equal(edges.Length, counter.Count);
        foreach (var edge in edges) Assert.Equal(1, drained[edge]);
    }

    [Fact]
    public void GrowthPreservesEveryPairAndCount()
    {
        // Far past the initial capacity, so this rehashes several times.
        const int distinct = 50_000;
        var counter = new PairCounter();
        for (int i = 0; i < distinct; i++)
            for (int rep = 0; rep <= i % 3; rep++)
                counter.Increment(i & 0xFFFF, (i * 7) & 0xFFFF);

        var expected = new Dictionary<(int, int), int>();
        for (int i = 0; i < distinct; i++)
        {
            var key = (i & 0xFFFF, (i * 7) & 0xFFFF);
            expected.TryGetValue(key, out int c);
            expected[key] = c + (i % 3) + 1;
        }

        var drained = Drain(counter);
        Assert.Equal(expected.Count, counter.Count);
        Assert.Equal(expected.Count, drained.Count);
        foreach (var kv in expected)
            Assert.Equal(kv.Value, drained[kv.Key]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    [InlineData(-8)]
    public void RejectsCapacityThatIsNotAPowerOfTwo(int capacity)
    {
        // Indexing masks with capacity-1, so a non-power-of-two would make linear probing skip
        // slots and spin forever on a table with room. Better to fail in the constructor.
        Assert.Throws<ArgumentException>(() => new PairCounter(capacity));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(1 << 13)]
    public void AcceptsPowerOfTwoCapacities(int capacity)
    {
        var counter = new PairCounter(capacity);
        for (int i = 0; i < 500; i++) counter.Increment(i, i + 1);
        Assert.Equal(500, counter.Count);
    }

    [Fact]
    public void EmptyCounterHasNoEntries()
    {
        var counter = new PairCounter();
        Assert.Equal(0, counter.Count);
        Assert.Empty(Drain(counter));
    }
}

public class Symbol16CandidateSetTests
{
    private static Symbol16 Sym(string s) => Symbol16.FromSpan(System.Text.Encoding.ASCII.GetBytes(s));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1000)]
    public void RejectsCapacityThatIsNotAPowerOfTwo(int capacity)
    {
        Assert.Throws<ArgumentException>(() => new Symbol16CandidateSet(capacity));
    }

    [Fact]
    public void GainsAccumulatePerDistinctSymbol()
    {
        var set = new Symbol16CandidateSet();
        set.Add(Sym("ab"), 5);
        set.Add(Sym("ab"), 7);
        set.Add(Sym("cd"), 2);

        Assert.Equal(2, set.Count);

        long abGain = 0, cdGain = 0;
        for (int i = 0; i < set.Symbols.Length; i++)
        {
            if (set.Symbols[i].Length() == 0) continue;
            if (set.Symbols[i].Lo == Sym("ab").Lo) abGain = set.Gains[i];
            if (set.Symbols[i].Lo == Sym("cd").Lo) cdGain = set.Gains[i];
        }

        Assert.Equal(12, abGain);
        Assert.Equal(2, cdGain);
    }

    [Fact]
    public void SameBytesWithDifferentLengthsAreDistinct()
    {
        // "a" and "a\0" share a Lo of 0x61 once zero-padded, so length must take part in identity.
        var set = new Symbol16CandidateSet();
        set.Add(Symbol16.FromSpan([(byte)'a', 0]), 1);
        set.Add(Symbol16.FromSpan([(byte)'a', 0, 0]), 1);

        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void GrowthPreservesEverySymbolAndGain()
    {
        const int distinct = 40_000;
        var set = new Symbol16CandidateSet();
        for (int i = 0; i < distinct; i++)
        {
            var s = Symbol16.FromSpan(BitConverter.GetBytes((long)i));
            set.Add(s, i);
            set.Add(s, 1);
        }

        Assert.Equal(distinct, set.Count);

        long total = 0;
        int occupied = 0;
        for (int i = 0; i < set.Symbols.Length; i++)
        {
            if (set.Symbols[i].Length() == 0) continue;
            occupied++;
            total += set.Gains[i];
        }

        Assert.Equal(distinct, occupied);
        Assert.Equal((long)distinct * (distinct - 1) / 2 + distinct, total);
    }
}
