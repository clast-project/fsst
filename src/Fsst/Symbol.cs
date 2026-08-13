// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Clast.Fsst;

/// <summary>
/// 8-byte symbol with packed code/length/ignoredBits field.
/// Val holds up to 8 bytes of symbol content (little-endian).
/// Icl packs: length:4 | code:12 | ignoredBits:16 (low bits).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Symbol
{
    public const int MaxLength = 8;

    /// <summary>Symbol content, up to 8 bytes, stored little-endian.</summary>
    public ulong Val;

    /// <summary>Packed field: (length &lt;&lt; 28) | (code &lt;&lt; 16) | ignoredBits.</summary>
    public ulong Icl;

    public const int CodeBits = 9;
    public const int CodeBase = 256;
    public const int CodeMax = 1 << CodeBits; // 512
    public const int CodeMask = CodeMax - 1;  // 0x1FF
    public const int LenBits = 12;

    public const uint IclFree = (15u << 28) | ((uint)CodeMask << 16);

    /// <summary>
    /// Code carried by a probe symbol — one built from input bytes rather than read from a table.
    /// <para>
    /// <c>FindLongestSymbol</c> decides a hash-table hit with <c>h.Icl &lt;= s.Icl</c>, which is meant
    /// to read as "the stored symbol is no longer than the input available here". Length occupies the
    /// top bits so it dominates, but when the two lengths are equal the comparison falls through to
    /// the code field — so the probe must carry a code at least as large as any stored code, or a
    /// stored symbol becomes unreachable exactly when it fits the input snugly.
    /// </para>
    /// <para>
    /// This is the largest value the 12-bit code field can hold, which covers both code spaces:
    /// FSST8 assigns at most 510 (<see cref="CodeMask"/> would do), and FSST12 assigns up to 4095
    /// (it would not). Probe codes are only ever compared, never read back.
    /// </para>
    /// </summary>
    public const int ProbeCode = 0xFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Length() => (int)(Icl >> 28);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Code() => (int)((Icl >> 16) & CodeMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int IgnoredBits() => (int)(Icl & 0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCodeLen(int code, int len)
    {
        Icl = ((ulong)(uint)len << 28) | ((ulong)(uint)(code & CodeMask) << 16) | (uint)((8 - len) * 8);
    }

    /// <summary>
    /// Stamp the length of a symbol that has no code of its own yet — a probe built from input, or a
    /// concatenation still being scored. Uses <see cref="ProbeCode"/> rather than
    /// <see cref="SetCodeLen"/>, whose 9-bit mask cannot represent it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetProbeLen(int len)
    {
        Icl = ((ulong)(uint)len << 28) | ((ulong)ProbeCode << 16) | (uint)((8 - len) * 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte First() => (byte)(Val & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort First2() => (ushort)(Val & 0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong Hash()
    {
        ulong v = Val & 0xFFFFFF;
        return FsstHash(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong FsstHash(ulong w)
    {
        const ulong prime = 2971215073UL;
        ulong h = unchecked(w * prime);
        return h ^ (h >> 15);
    }

    /// <summary>Create a single-byte symbol with the given code.</summary>
    public static Symbol FromByte(byte c, int code)
    {
        var s = new Symbol();
        s.Val = c;
        s.Icl = (1UL << 28) | ((ulong)(code & CodeMask) << 16) | 56UL; // ignoredBits = (8-1)*8 = 56
        return s;
    }

    /// <summary>Create a symbol from a span of bytes (up to 8).</summary>
    public static unsafe Symbol FromSpan(ReadOnlySpan<byte> input)
    {
        int len = Math.Min(input.Length, MaxLength);
        var s = new Symbol();
        fixed (byte* ptr = input)
            s.Val = len >= 8 ? Unsafe.ReadUnaligned<ulong>(ptr) : LoadTail(ptr, len);
        s.SetProbeLen(len);
        return s;
    }

    /// <summary>Create a symbol from a pointer and length.</summary>
    public static unsafe Symbol FromPointer(byte* ptr, int available)
    {
        int len = available < MaxLength ? available : MaxLength;
        var s = new Symbol();
        s.Val = available >= 8 ? Unsafe.ReadUnaligned<ulong>(ptr) : LoadTail(ptr, len);
        s.SetProbeLen(len);
        return s;
    }

    /// <summary>
    /// Assemble fewer than 8 bytes into a little-endian <see cref="ulong"/>, zero-padded.
    /// </summary>
    /// <remarks>
    /// Reads as wide as is safe rather than going through a cleared stack buffer. This runs for the
    /// last few bytes of every value, so for the short values FSST is aimed at it is a large share
    /// of all probe positions, not a rare tail case.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe ulong LoadTail(byte* ptr, int len)
    {
        if (len >= 4)
        {
            ulong v = Unsafe.ReadUnaligned<uint>(ptr);
            if (len >= 6)
            {
                v |= (ulong)Unsafe.ReadUnaligned<ushort>(ptr + 4) << 32;
                if (len == 7) v |= (ulong)ptr[6] << 48;
            }
            else if (len == 5)
            {
                v |= (ulong)ptr[4] << 32;
            }
            return v;
        }

        if (len >= 2)
        {
            ulong v = Unsafe.ReadUnaligned<ushort>(ptr);
            if (len == 3) v |= (ulong)ptr[2] << 16;
            return v;
        }

        return len == 1 ? ptr[0] : 0UL;
    }

    /// <summary>Concatenate two symbols, truncating at 8 bytes.</summary>
    public static Symbol Concat(in Symbol a, in Symbol b)
    {
        int lenA = a.Length();
        int lenB = b.Length();
        int total = Math.Min(lenA + lenB, MaxLength);

        var s = new Symbol();
        s.Val = a.Val | (b.Val << (lenA * 8));
        s.SetProbeLen(total);
        return s;
    }

    /// <summary>Create a "free" hash table entry.</summary>
    public static Symbol Free()
    {
        return new Symbol { Val = 0, Icl = IclFree };
    }

    public readonly bool IsFree() => Icl >= IclFree;
}
