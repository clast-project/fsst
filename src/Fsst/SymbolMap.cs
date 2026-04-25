// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Fsst;

/// <summary>
/// FSST12 symbol table: up to 4096 symbols with 12-bit codes.
/// Codes 0-255 always map to single bytes.
/// No escape mechanism — all values are valid codes.
/// </summary>
public sealed class SymbolMap
{
    internal const int CodeBits12 = 12;
    internal const int CodeMax12 = 1 << CodeBits12; // 4096
    internal const int CodeMask12 = CodeMax12 - 1;
    internal const int CodeBase12 = 256;
    internal const int HashTabSize = 1 << 14; // 16384

    /// <summary>2-byte prefix lookup: (len &lt;&lt; 12) | code.</summary>
    internal readonly ushort[] ShortCodes = new ushort[65536];

    /// <summary>All symbols. 0-255 are single-byte, 256+ are multi-byte.</summary>
    internal readonly Symbol[] Symbols = new Symbol[CodeMax12];

    /// <summary>Lossy hash table for length-3+ symbols.</summary>
    internal readonly Symbol[] HashTab = new Symbol[HashTabSize];

    /// <summary>Number of real symbols beyond the 256 base codes.</summary>
    internal int NSymbols;

    internal readonly int[] LenHisto = new int[8];

    internal SymbolMap()
    {
        NSymbols = 0;

        // Initialize single-byte symbols
        for (int i = 0; i < 256; i++)
        {
            var s = Symbol.FromByte((byte)i, i);
            Symbols[i] = s;
        }

        // Mark unused
        for (int i = 256; i < CodeMax12; i++)
            Symbols[i] = Symbol.Free();

        // Hash table free
        for (int i = 0; i < HashTabSize; i++)
            HashTab[i] = Symbol.Free();

        // shortCodes: each 2-byte value maps to low byte with length 1
        for (int i = 0; i < 65536; i++)
            ShortCodes[i] = (ushort)((1 << CodeBits12) | (i & 255));

        Array.Clear(LenHisto, 0, LenHisto.Length);
    }

    internal void Clear()
    {
        Array.Clear(LenHisto, 0, LenHisto.Length);
        for (int i = CodeBase12; i < CodeBase12 + NSymbols; i++)
        {
            int len = Symbols[i].Length();
            if (len == 2)
            {
                ushort first2 = Symbols[i].First2();
                ShortCodes[first2] = (ushort)((1 << CodeBits12) | (first2 & 255));
            }
            else if (len >= 3)
            {
                int idx = (int)(Symbols[i].Hash() & (HashTabSize - 1));
                HashTab[idx] = Symbol.Free();
            }
        }
        NSymbols = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HashInsert(Symbol s)
    {
        int idx = (int)(s.Hash() & (HashTabSize - 1));
        if (HashTab[idx].Icl < Symbol.IclFree)
            return false;
        HashTab[idx].Icl = s.Icl;
        HashTab[idx].Val = s.Val & (0xFFFFFFFFFFFFFFFF >> (int)(byte)s.Icl);
        return true;
    }

    internal bool Add(Symbol s)
    {
        if (CodeBase12 + NSymbols >= CodeMax12)
            return false;

        int len = s.Length();
        int code = CodeBase12 + NSymbols;

        // Use 12-bit code packing: (len << 28) | (code << 16) | ignoredBits
        s.Icl = ((ulong)(uint)len << 28) | ((ulong)(uint)(code & CodeMask12) << 16) | (uint)((8 - len) * 8);

        if (len == 1)
        {
            // Single bytes are already covered by codes 0-255
            return false;
        }
        else if (len == 2)
        {
            ShortCodes[s.First2()] = (ushort)((len << CodeBits12) | code);
        }
        else if (!HashInsert(s))
        {
            return false;
        }

        Symbols[code] = s;
        NSymbols++;
        LenHisto[len - 1]++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int FindLongestSymbol(Symbol s)
    {
        // Check hash table for 3+ byte matches
        int idx = (int)(s.Hash() & (HashTabSize - 1));
        ref Symbol h = ref HashTab[idx];
        if (h.Icl <= s.Icl &&
            h.Val == (s.Val & (0xFFFFFFFFFFFFFFFF >> (int)(byte)h.Icl)))
        {
            return (int)((h.Icl >> 16) & CodeMask12);
        }

        // Check 2-byte shortcodes
        if (s.Length() >= 2)
        {
            ushort sc = ShortCodes[s.First2()];
            int code = sc & CodeMask12;
            if (code >= CodeBase12)
                return code;
        }

        // Single byte fallback
        return s.First();
    }
}
