using System.Numerics;
using System.Runtime.CompilerServices;

namespace Fsst;

/// <summary>
/// Split-byte frequency counters for FSST8 table building.
/// Counts are split into high/low bytes to enable fast iteration
/// over non-zero entries using trailing zero count.
/// </summary>
public sealed class Counters
{
    private const int CodeMax = Symbol.CodeMax; // 512

    // Single-symbol counters (split high/low bytes)
    public readonly byte[] Count1High = new byte[CodeMax];
    public readonly byte[] Count1Low = new byte[CodeMax];

    // Pair counters (split high/low bytes)
    // count2High[pos1][pos2>>1] uses 4-bit nibbles
    public readonly byte[] Count2High = new byte[CodeMax * (CodeMax / 2)];
    // count2Low[pos1][pos2]
    public readonly byte[] Count2Low = new byte[CodeMax * CodeMax];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Count1Set(int pos1, int val)
    {
        Count1Low[pos1] = (byte)(val & 255);
        Count1High[pos1] = (byte)(val >> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Count1Inc(int pos1)
    {
        // C++ semantics: !count1Low[pos1]++ (post-increment, check old value == 0)
        byte old = Count1Low[pos1];
        Count1Low[pos1] = (byte)(old + 1);
        if (old == 0)
            Count1High[pos1]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Count2Inc(int pos1, int pos2)
    {
        byte old = Count2Low[pos1 * CodeMax + pos2];
        Count2Low[pos1 * CodeMax + pos2] = (byte)(old + 1);
        if (old == 0)
            Count2High[pos1 * (CodeMax / 2) + (pos2 >> 1)] += (byte)(1 << ((pos2 & 1) << 2));
    }

    /// <summary>
    /// Get next non-zero count1 entry starting at pos1.
    /// Advances pos1 to the found position. Returns 0 if none found.
    /// </summary>
    public int Count1GetNext(ref int pos1)
    {
        if (pos1 >= CodeMax) return 0;

        // Scan high bytes 8 at a time using trailing zero count
        while (pos1 < CodeMax)
        {
            // Read up to 8 bytes of count1High
            ulong high = 0;
            int remaining = Math.Min(8, CodeMax - pos1);
            for (int i = 0; i < remaining; i++)
                high |= (ulong)Count1High[pos1 + i] << (i * 8);

            if (high != 0)
            {
                int zero = BitOperations.TrailingZeroCount(high) >> 3;
                pos1 += zero;
                if (pos1 >= CodeMax) return 0;

                int hi = Count1High[pos1];
                int lo = Count1Low[pos1];
                if (lo != 0) hi--;
                return (hi << 8) + lo;
            }
            pos1 += 8;
        }
        return 0;
    }

    /// <summary>
    /// Get next non-zero count2 entry for pos1, starting at pos2.
    /// Advances pos2. Returns 0 if none found.
    /// </summary>
    public int Count2GetNext(int pos1, ref int pos2)
    {
        if (pos2 >= CodeMax) return 0;

        int baseH = pos1 * (CodeMax / 2);

        while (pos2 < CodeMax)
        {
            ulong high = 0;
            int halfPos = pos2 >> 1;
            int remaining = Math.Min(8, (CodeMax / 2) - halfPos);
            if (remaining <= 0) break;

            for (int i = 0; i < remaining; i++)
                high |= (ulong)Count2High[baseH + halfPos + i] << (i * 8);

            high >>= (pos2 & 1) << 2;

            if (high != 0)
            {
                int zero = BitOperations.TrailingZeroCount(high) >> 2;
                pos2 += zero;
                if (pos2 >= CodeMax) return 0;

                int nibble = (Count2High[baseH + (pos2 >> 1)] >> ((pos2 & 1) << 2)) & 0xF;
                int lo = Count2Low[pos1 * CodeMax + pos2];
                if (lo != 0) nibble--;
                return (nibble << 8) + lo;
            }
            pos2 += (remaining * 2) - (pos2 & 1);
        }
        return 0;
    }

    public void Clear()
    {
        Array.Clear(Count1High);
        Array.Clear(Count1Low);
        Array.Clear(Count2High);
        Array.Clear(Count2Low);
    }
}
