// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Fsst.Tests;

public class CountersTests
{
    private static int Count2(Counters counters, int pos1, int pos2)
    {
        int pos = pos2;
        int count = counters.Count2GetNext(pos1, ref pos);
        return pos == pos2 ? count : 0; // the scan moved past pos2, so pos2 itself was zero
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(512)]
    [InlineData(3839)]
    [InlineData(3840)]
    public void Count2Inc_IsExactUpToTheCeiling(int increments)
    {
        var counters = new Counters();
        for (int i = 0; i < increments; i++)
            counters.Count2Inc(97, 98);

        Assert.Equal(increments, Count2(counters, 97, 98));
    }

    [Theory]
    [InlineData(3841)]
    [InlineData(4096)]
    [InlineData(5000)]
    [InlineData(20000)]
    public void Count2Inc_SaturatesWithoutCarryingIntoTheNeighbouringPair(int increments)
    {
        // Regression for #3. Pair counts are 12 bits — a 4-bit nibble over an 8-bit low byte, with
        // nibbles packed two per byte. Incrementing a nibble already at 15 used to carry into the
        // adjacent nibble, which belongs to pair (pos1, pos2 ^ 1): the real count was destroyed and
        // a pair that never occurred was handed a phantom one.
        var counters = new Counters();
        for (int i = 0; i < increments; i++)
            counters.Count2Inc(97, 98);

        Assert.Equal(Counters.Count2Max, Count2(counters, 97, 98));

        // 98 is even, so it owns the low nibble and would carry into 99's high nibble.
        Assert.Equal(0, Count2(counters, 97, 99));
    }

    [Fact]
    public void Count2Inc_OddPositionSaturatesWithoutCarryingIntoTheNextByte()
    {
        // An odd pos2 owns the high nibble, so its carry would spill into the next byte entirely —
        // pair (pos1, pos2 + 1).
        var counters = new Counters();
        for (int i = 0; i < 5000; i++)
            counters.Count2Inc(97, 99);

        Assert.Equal(Counters.Count2Max, Count2(counters, 97, 99));
        Assert.Equal(0, Count2(counters, 97, 98));
        Assert.Equal(0, Count2(counters, 97, 100));
    }

    [Fact]
    public void Count2Inc_SaturatedPairDoesNotDisturbOtherPairs()
    {
        var counters = new Counters();
        for (int i = 0; i < 10000; i++)
            counters.Count2Inc(97, 98);
        for (int i = 0; i < 7; i++)
            counters.Count2Inc(97, 99);

        Assert.Equal(Counters.Count2Max, Count2(counters, 97, 98));
        Assert.Equal(7, Count2(counters, 97, 99));
    }

    [Fact]
    public void Count2Inc_SaturationIsPerPairNotPerRow()
    {
        var counters = new Counters();
        for (int i = 0; i < 10000; i++)
            counters.Count2Inc(97, 98);
        for (int i = 0; i < 10000; i++)
            counters.Count2Inc(200, 201);

        Assert.Equal(Counters.Count2Max, Count2(counters, 97, 98));
        Assert.Equal(Counters.Count2Max, Count2(counters, 200, 201));
        Assert.Equal(0, Count2(counters, 97, 99));
        Assert.Equal(0, Count2(counters, 200, 202));
    }
}
