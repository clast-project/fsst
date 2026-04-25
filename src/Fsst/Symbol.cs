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
    public static Symbol FromSpan(ReadOnlySpan<byte> input)
    {
        var s = new Symbol();
        int len = Math.Min(input.Length, MaxLength);
        s.Val = 0;
        if (len >= 8)
        {
            s.Val = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(input));
        }
        else
        {
            // Copy byte by byte for short spans
            Span<byte> buf = stackalloc byte[8];
            buf.Clear();
            input[..len].CopyTo(buf);
            s.Val = Unsafe.ReadUnaligned<ulong>(ref buf[0]);
        }
        s.SetCodeLen(CodeMax, len);
        return s;
    }

    /// <summary>Create a symbol from a pointer and length.</summary>
    public static unsafe Symbol FromPointer(byte* ptr, int available)
    {
        var s = new Symbol();
        int len = Math.Min(available, MaxLength);
        s.Val = 0;
        if (available >= 8)
        {
            s.Val = Unsafe.ReadUnaligned<ulong>(ptr);
        }
        else
        {
            Span<byte> buf = stackalloc byte[8];
            buf.Clear();
            new ReadOnlySpan<byte>(ptr, len).CopyTo(buf);
            s.Val = Unsafe.ReadUnaligned<ulong>(ref buf[0]);
        }
        s.SetCodeLen(CodeMax, len);
        return s;
    }

    /// <summary>Concatenate two symbols, truncating at 8 bytes.</summary>
    public static Symbol Concat(in Symbol a, in Symbol b)
    {
        int lenA = a.Length();
        int lenB = b.Length();
        int total = Math.Min(lenA + lenB, MaxLength);

        var s = new Symbol();
        s.Val = a.Val | (b.Val << (lenA * 8));
        s.SetCodeLen(CodeMax, total);
        return s;
    }

    /// <summary>Create a "free" hash table entry.</summary>
    public static Symbol Free()
    {
        return new Symbol { Val = 0, Icl = IclFree };
    }

    public readonly bool IsFree() => Icl >= IclFree;
}
