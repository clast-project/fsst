// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Clast.Fsst;

/// <summary>
/// FSST8 symbol table: up to 255 symbols with 9-bit codes (256-510).
/// Codes 0-255 are single-byte pseudo-symbols (escaped or real).
/// Code 255 is the escape byte in compressed output.
/// </summary>
public sealed class SymbolTable
{
    internal const int HashTabSize = 1 << 10; // 1024
    internal const int EscCode = 255;

    /// <summary>2-byte prefix lookup: shortCodes[first2bytes] = (len &lt;&lt; 12) | code.</summary>
    internal readonly ushort[] ShortCodes = new ushort[65536];

    /// <summary>Single-byte lookup: byteCodes[byte] = (1 &lt;&lt; 12) | code.</summary>
    internal readonly ushort[] ByteCodes = new ushort[256];

    /// <summary>All symbols. 0-255 are single-byte pseudo-symbols, 256+ are real multi-byte symbols.</summary>
    internal readonly Symbol[] Symbols = new Symbol[Symbol.CodeMax];

    /// <summary>Lossy hash table for length-3+ symbols.</summary>
    internal readonly Symbol[] HashTab = new Symbol[HashTabSize];

    /// <summary>Number of real symbols (beyond the 256 base single-byte codes).</summary>
    internal int NSymbols;

    internal int SuffixLim;
    internal int Terminator;
    internal bool ZeroTerminated;
    internal readonly int[] LenHisto = new int[Symbol.CodeBits]; // 9 entries

    internal SymbolTable()
    {
        NSymbols = 0;
        SuffixLim = Symbol.CodeMax;
        Terminator = 0;
        ZeroTerminated = false;

        // Initialize single-byte symbols (codes 0-255) with special encoding
        for (int i = 0; i < 256; i++)
        {
            Symbols[i] = Symbol.FromByte((byte)i, i | (1 << Symbol.LenBits));
        }

        // Mark unused slots
        var unused = Symbol.FromByte(0, Symbol.CodeMask);
        for (int i = 256; i < Symbol.CodeMax; i++)
        {
            Symbols[i] = unused;
        }

        // Initialize hash table as free
        for (int i = 0; i < HashTabSize; i++)
        {
            HashTab[i] = Symbol.Free();
        }

        // Initialize byteCodes: each byte maps to itself with length 1
        for (int i = 0; i < 256; i++)
        {
            ByteCodes[i] = (ushort)((1 << Symbol.LenBits) | i);
        }

        // Initialize shortCodes: each 2-byte value maps to its low byte with length 1
        for (int i = 0; i < 65536; i++)
        {
            ShortCodes[i] = (ushort)((1 << Symbol.LenBits) | (i & 255));
        }

        Array.Clear(LenHisto, 0, LenHisto.Length);
    }

    /// <summary>Clear all real symbols, resetting to base state.</summary>
    internal void Clear()
    {
        Array.Clear(LenHisto, 0, LenHisto.Length);
        for (int i = Symbol.CodeBase; i < Symbol.CodeBase + NSymbols; i++)
        {
            int len = Symbols[i].Length();
            if (len == 1)
            {
                int val = Symbols[i].First();
                ByteCodes[val] = (ushort)((1 << Symbol.LenBits) | val);
            }
            else if (len == 2)
            {
                int val = Symbols[i].First2();
                ShortCodes[val] = (ushort)((1 << Symbol.LenBits) | (val & 255));
            }
            else
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
            return false; // slot taken

        HashTab[idx].Icl = s.Icl;
        HashTab[idx].Val = s.Val & (0xFFFFFFFFFFFFFFFF >> (int)(byte)s.Icl);
        return true;
    }

    internal bool Add(Symbol s)
    {
        if (Symbol.CodeBase + NSymbols >= Symbol.CodeMax)
            return false;

        int len = s.Length();
        s.SetCodeLen(Symbol.CodeBase + NSymbols, len);

        if (len == 1)
        {
            ByteCodes[s.First()] = (ushort)(Symbol.CodeBase + NSymbols + (1 << Symbol.LenBits));
        }
        else if (len == 2)
        {
            ShortCodes[s.First2()] = (ushort)(Symbol.CodeBase + NSymbols + (2 << Symbol.LenBits));
        }
        else if (!HashInsert(s))
        {
            return false;
        }

        Symbols[Symbol.CodeBase + NSymbols++] = s;
        LenHisto[len - 1]++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int FindLongestSymbol(Symbol s)
    {
        int idx = (int)(s.Hash() & (HashTabSize - 1));
        ref Symbol h = ref HashTab[idx];
        if (h.Icl <= s.Icl &&
            h.Val == (s.Val & (0xFFFFFFFFFFFFFFFF >> (int)(byte)h.Icl)))
        {
            return (int)((h.Icl >> 16) & Symbol.CodeMask);
        }

        if (s.Length() >= 2)
        {
            int code = ShortCodes[s.First2()] & Symbol.CodeMask;
            if (code >= Symbol.CodeBase)
                return code;
        }

        return ByteCodes[s.First()] & Symbol.CodeMask;
    }

    /// <summary>
    /// Finalize the symbol table: reorder codes by length groups,
    /// populate shortCodes for single-byte fallback.
    /// </summary>
    internal void Finalize(bool zeroTerminated)
    {
        this.ZeroTerminated = zeroTerminated;

        if (NSymbols == 0)
        {
            // No symbols: all bytes must be escaped
            for (int i = 0; i < 256; i++)
                ByteCodes[i] = (ushort)(511 + (1 << Symbol.LenBits));
            for (int i = 0; i < 65536; i++)
                ShortCodes[i] = (ushort)(511 + (1 << Symbol.LenBits));
            return;
        }

        int zt = zeroTerminated ? 1 : 0;
        int byteLim = NSymbols - (LenHisto[0] - zt);

        Span<int> newCode = stackalloc int[256];
        Span<int> rsum = stackalloc int[8];

        rsum[0] = byteLim;
        rsum[1] = zt;
        for (int i = 1; i < 7; i++)
            rsum[i + 1] = rsum[i] + LenHisto[i];

        SuffixLim = rsum[1];

        // Process symbols
        for (int i = zt; i < NSymbols; i++)
        {
            var s1 = Symbols[Symbol.CodeBase + i];
            int len = s1.Length();
            bool isTwoByte = len == 2;
            int opt = isTwoByte ? NSymbols : 0;

            if (isTwoByte)
            {
                ushort first2 = s1.First2();
                for (int k = 0; k < NSymbols; k++)
                {
                    if (k != i && Symbols[Symbol.CodeBase + k].Length() > 1 &&
                        first2 == Symbols[Symbol.CodeBase + k].First2())
                    {
                        opt = 0;
                        break;
                    }
                }
                if (opt != 0)
                    newCode[i] = SuffixLim++;
                else
                    newCode[i] = --rsum[2]; // j starts at rsum[2], goes down
            }
            else
            {
                newCode[i] = rsum[len - 1]++;
            }

            s1.SetCodeLen(newCode[i], len);
            Symbols[newCode[i]] = s1;
        }

        // Update byteCodes
        for (int i = 0; i < 256; i++)
        {
            if ((ByteCodes[i] & Symbol.CodeMask) >= Symbol.CodeBase)
                ByteCodes[i] = (ushort)(newCode[ByteCodes[i] & 0xFF] + (1 << Symbol.LenBits));
            else
                ByteCodes[i] = (ushort)(511 + (1 << Symbol.LenBits)); // escape code 511 = 255 + 256... actually maps to 255 after mask
        }

        // Update shortCodes
        for (int i = 0; i < 65536; i++)
        {
            if ((ShortCodes[i] & Symbol.CodeMask) >= Symbol.CodeBase)
                ShortCodes[i] = (ushort)(newCode[ShortCodes[i] & 0xFF] + (ShortCodes[i] & (15 << Symbol.LenBits)));
            else
                ShortCodes[i] = ByteCodes[i & 0xFF];
        }

        // Update hashTab
        for (int i = 0; i < HashTabSize; i++)
        {
            if (HashTab[i].Icl < Symbol.IclFree)
                HashTab[i] = Symbols[newCode[(byte)HashTab[i].Code()]];
        }
    }
}
