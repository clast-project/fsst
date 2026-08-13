// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Clast.Fsst;

/// <summary>
/// 16-byte symbol value with a packed code/length field, used by the FSST16 path.
/// <see cref="Lo"/> holds bytes 0-7 and <see cref="Hi"/> bytes 8-15, both little-endian and
/// zero-padded above the symbol's length.
/// Icl packs: length:5 (bits 40+) | code:16 (bits 16-31).
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="Symbol"/> rather than a widening of it: FSST8 and
/// FSST12 cap symbols at 8 bytes and pack length/code into the bit positions cwida uses, which
/// leaves no room for a 16-byte value or a 16-bit code.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct Symbol16
{
    public const int MaxLength = 16;

    public const int CodeBits = 16;
    public const int CodeMax = 1 << CodeBits;   // 65536
    public const int EscCode = CodeMax - 1;     // 65535
    public const int MaxSymbols = CodeMax - 1;  // codes 0..65534 are symbols

    private const int LenShift = 40;
    private const int CodeShift = 16;

    /// <summary>Bytes 0-7 of the symbol, little-endian.</summary>
    public ulong Lo;

    /// <summary>Bytes 8-15 of the symbol, little-endian.</summary>
    public ulong Hi;

    /// <summary>Packed field: (length &lt;&lt; 40) | (code &lt;&lt; 16).</summary>
    public ulong Icl;

    /// <summary>
    /// Sentinel for an empty hash slot. Larger than any real or probe Icl, so the
    /// <c>h.Icl &lt;= s.Icl</c> test in <c>FindLongestSymbol</c> rejects free slots.
    /// </summary>
    public const ulong IclFree = (31UL << LenShift) | ((ulong)EscCode << CodeShift) | 0xFFFF;

    /// <summary>Mask keeping the low <c>i</c> bytes of <see cref="Lo"/>, indexed by symbol length.</summary>
    public static readonly ulong[] LoMask = BuildLoMask();

    /// <summary>Mask keeping the low <c>i - 8</c> bytes of <see cref="Hi"/>, indexed by symbol length.</summary>
    public static readonly ulong[] HiMask = BuildHiMask();

    private static ulong[] BuildLoMask()
    {
        var m = new ulong[MaxLength + 1];
        for (int len = 0; len <= MaxLength; len++)
            m[len] = len >= 8 ? ulong.MaxValue : (1UL << (len * 8)) - 1;
        return m;
    }

    private static ulong[] BuildHiMask()
    {
        var m = new ulong[MaxLength + 1];
        for (int len = 0; len <= MaxLength; len++)
            m[len] = len <= 8 ? 0UL : (len == 16 ? ulong.MaxValue : (1UL << ((len - 8) * 8)) - 1);
        return m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Length() => (int)(Icl >> LenShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Code() => (int)((Icl >> CodeShift) & EscCode);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCodeLen(int code, int len)
    {
        Icl = ((ulong)(uint)len << LenShift) | ((ulong)(uint)(code & EscCode) << CodeShift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte First() => (byte)(Lo & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort First2() => (ushort)(Lo & 0xFFFF);

    /// <summary>Hash of the first 3 bytes, matching the FSST8/FSST12 hash so behaviour stays comparable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong Hash() => Symbol.FsstHash(Lo & 0xFFFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsFree() => Icl >= IclFree;

    /// <summary>Zero every byte at or above <paramref name="len"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Truncate(int len)
    {
        Lo &= LoMask[len];
        Hi &= HiMask[len];
    }

    /// <summary>Create a single-byte symbol. The code is assigned when it is added to a table.</summary>
    public static Symbol16 FromByte(byte c)
    {
        var s = new Symbol16 { Lo = c, Hi = 0 };
        s.SetCodeLen(EscCode, 1);
        return s;
    }

    /// <summary>Create a symbol from up to 16 bytes.</summary>
    public static unsafe Symbol16 FromSpan(ReadOnlySpan<byte> input)
    {
        int len = Math.Min(input.Length, MaxLength);
        var s = new Symbol16();
        fixed (byte* ptr = input)
            Load(ptr, len, ref s);
        s.SetCodeLen(EscCode, len);
        return s;
    }

    /// <summary>
    /// Create a probe symbol covering up to 16 bytes at <paramref name="ptr"/>.
    /// The code is set to <see cref="EscCode"/> — the maximum — so that a stored symbol of equal
    /// length always satisfies the <c>h.Icl &lt;= s.Icl</c> ordering test.
    /// </summary>
    public static unsafe Symbol16 FromPointer(byte* ptr, int available)
    {
        var s = new Symbol16();
        int len = available < MaxLength ? available : MaxLength;
        Load(ptr, len, ref s);
        s.SetCodeLen(EscCode, len);
        return s;
    }

    /// <summary>
    /// Fill <paramref name="s"/>'s value from <paramref name="len"/> bytes, zero-padded to 16.
    /// </summary>
    /// <remarks>
    /// The 16-byte probe means anything within 15 bytes of the end of a value takes the narrow
    /// path, which for short values is most positions — so this reads as wide as is safe instead of
    /// going through a cleared stack buffer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Load(byte* ptr, int len, ref Symbol16 s)
    {
        if (len >= MaxLength)
        {
            s.Lo = Unsafe.ReadUnaligned<ulong>(ptr);
            s.Hi = Unsafe.ReadUnaligned<ulong>(ptr + 8);
        }
        else if (len >= 8)
        {
            s.Lo = Unsafe.ReadUnaligned<ulong>(ptr);
            s.Hi = Symbol.LoadTail(ptr + 8, len - 8);
        }
        else
        {
            s.Lo = Symbol.LoadTail(ptr, len);
            s.Hi = 0;
        }
    }

    /// <summary>Concatenate two symbols, truncating at <paramref name="maxLength"/> bytes.</summary>
    public static Symbol16 Concat(in Symbol16 a, in Symbol16 b, int maxLength)
    {
        int lenA = a.Length();
        int total = Math.Min(lenA + b.Length(), maxLength);

        ulong lo = a.Lo;
        ulong hi = a.Hi;
        int shift = lenA * 8;
        if (shift < 64)
        {
            lo |= b.Lo << shift;
            hi |= (b.Lo >> (64 - shift)) | (b.Hi << shift);
        }
        else if (shift < 128)
        {
            hi |= b.Lo << (shift - 64);
        }

        var s = new Symbol16 { Lo = lo, Hi = hi };
        s.Truncate(total);
        s.SetCodeLen(EscCode, total);
        return s;
    }

    /// <summary>Create a "free" hash table entry.</summary>
    public static Symbol16 Free() => new Symbol16 { Lo = 0, Hi = 0, Icl = IclFree };
}
