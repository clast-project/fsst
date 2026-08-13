// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Fsst;

/// <summary>
/// Open-addressing counter for adjacent code pairs, used while training FSST12 and FSST16 tables.
/// </summary>
/// <remarks>
/// FSST8 counts pairs in a dense <see cref="Counters"/> matrix, which a 4,096- or 65,536-wide code
/// space cannot afford. The obvious substitute — <c>Dictionary&lt;(int, int), int&gt;</c> — costs two
/// hash lookups per input position (<c>TryGetValue</c> then the indexer), and the pair count is the
/// hottest loop in training. This does one probe instead, on a flat pair of arrays.
/// </remarks>
internal sealed class PairCounter
{
    private const long Empty = -1;

    private long[] _keys;
    private int[] _counts;
    private int _occupied;
    private int _mask;

    public PairCounter(int initialCapacity = 1 << 13)
    {
        // Slots are indexed with `& _mask`, so a capacity that is not a power of two makes linear
        // probing skip slots: it can then spin forever on a table that still has room. Fail here
        // rather than hang later.
        if (initialCapacity < 2 || (initialCapacity & (initialCapacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two, and at least 2.", nameof(initialCapacity));

        _keys = new long[initialCapacity];
        _counts = new int[initialCapacity];
        _mask = initialCapacity - 1;
        _keys.AsSpan().Fill(Empty);
    }

    /// <summary>Number of distinct pairs recorded.</summary>
    public int Count => _occupied;

    /// <summary>Slot keys; <c>-1</c> marks an empty slot. Paired with <see cref="Counts"/> by index.</summary>
    public long[] Keys => _keys;

    /// <summary>Slot counts, meaningful only where <see cref="Keys"/> is not <c>-1</c>.</summary>
    public int[] Counts => _counts;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pos1(long key) => (int)(key >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pos2(long key) => (int)(key & 0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(long key)
    {
        // Mixing constant from splitmix64; the low bits of a packed pair are far from uniform.
        ulong h = unchecked((ulong)key * 0x9E3779B97F4A7C15UL);
        return (int)((h ^ (h >> 29)) & int.MaxValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(int pos1, int pos2)
    {
        long key = ((long)pos1 << 16) | (uint)pos2;
        int i = Hash(key) & _mask;

        while (true)
        {
            long k = _keys[i];
            if (k == key)
            {
                _counts[i]++;
                return;
            }
            if (k == Empty)
            {
                _keys[i] = key;
                _counts[i] = 1;
                // Grow at a 0.7 load factor; linear probing degrades sharply past that.
                if (++_occupied * 10 > (_mask + 1) * 7) Grow();
                return;
            }
            i = (i + 1) & _mask;
        }
    }

    private void Grow()
    {
        var oldKeys = _keys;
        var oldCounts = _counts;

        int capacity = (_mask + 1) * 2;
        _keys = new long[capacity];
        _counts = new int[capacity];
        _mask = capacity - 1;
        _keys.AsSpan().Fill(Empty);

        for (int i = 0; i < oldKeys.Length; i++)
        {
            long key = oldKeys[i];
            if (key == Empty) continue;

            int j = Hash(key) & _mask;
            while (_keys[j] != Empty) j = (j + 1) & _mask;
            _keys[j] = key;
            _counts[j] = oldCounts[i];
        }
    }
}
