// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Fsst;

/// <summary>
/// Accumulates candidate symbols and their gains while training an FSST16 table, deduplicating by
/// symbol value and length.
/// </summary>
/// <remarks>
/// The natural spelling is <c>Dictionary&lt;(ulong, ulong, int), (Symbol16, long)&gt;</c>, but that
/// pairs a 20-byte key with a 32-byte value and costs two hash lookups per candidate
/// (<c>TryGetValue</c> then the indexer). Candidates are generated once per symbol and once per
/// adjacent pair above threshold, so this is the second-hottest loop in training after the pair
/// count. Open addressing over flat arrays does one probe and keeps the payload contiguous.
/// <para>
/// A default <see cref="Symbol16"/> has length 0 and candidates always have length 2 or more, so a
/// zeroed slot reads as empty with no fill pass.
/// </para>
/// </remarks>
internal sealed class Symbol16CandidateSet
{
    private Symbol16[] _symbols;
    private long[] _gains;
    private int _occupied;
    private int _mask;

    public Symbol16CandidateSet(int initialCapacity = 1 << 13)
    {
        // Slots are indexed with `& _mask`, so a capacity that is not a power of two makes linear
        // probing skip slots: it can then spin forever on a table that still has room. Fail here
        // rather than hang later.
        if (initialCapacity < 2 || (initialCapacity & (initialCapacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two, and at least 2.", nameof(initialCapacity));

        _symbols = new Symbol16[initialCapacity];
        _gains = new long[initialCapacity];
        _mask = initialCapacity - 1;
    }

    /// <summary>Number of distinct candidates held.</summary>
    public int Count => _occupied;

    /// <summary>Slots; a slot with <c>Length() == 0</c> is empty. Paired with <see cref="Gains"/> by index.</summary>
    public Symbol16[] Symbols => _symbols;

    /// <summary>Accumulated gain per slot, meaningful only where the slot is occupied.</summary>
    public long[] Gains => _gains;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ulong lo, ulong hi, int len)
    {
        ulong h = lo * 0x9E3779B97F4A7C15UL;
        h ^= (hi + 0x165667B19E3779F9UL) * 0xC2B2AE3D27D4EB4FUL;
        h ^= (ulong)len << 56;
        h ^= h >> 31;
        return (int)(h & int.MaxValue);
    }

    /// <summary>Add <paramref name="gain"/> to <paramref name="s"/>'s running total.</summary>
    public void Add(in Symbol16 s, long gain)
    {
        int len = s.Length();
        int i = Hash(s.Lo, s.Hi, len) & _mask;

        while (true)
        {
            ref Symbol16 slot = ref _symbols[i];
            int slotLen = slot.Length();

            if (slotLen == 0)
            {
                slot = s;
                _gains[i] = gain;
                if (++_occupied * 10 > (_mask + 1) * 7) Grow();
                return;
            }

            if (slotLen == len && slot.Lo == s.Lo && slot.Hi == s.Hi)
            {
                _gains[i] += gain;
                return;
            }

            i = (i + 1) & _mask;
        }
    }

    private void Grow()
    {
        var oldSymbols = _symbols;
        var oldGains = _gains;

        int capacity = (_mask + 1) * 2;
        _symbols = new Symbol16[capacity];
        _gains = new long[capacity];
        _mask = capacity - 1;

        for (int i = 0; i < oldSymbols.Length; i++)
        {
            ref Symbol16 s = ref oldSymbols[i];
            int len = s.Length();
            if (len == 0) continue;

            int j = Hash(s.Lo, s.Hi, len) & _mask;
            while (_symbols[j].Length() != 0) j = (j + 1) & _mask;
            _symbols[j] = s;
            _gains[j] = oldGains[i];
        }
    }
}
