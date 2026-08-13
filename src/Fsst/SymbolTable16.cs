// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Clast.Fsst;

/// <summary>
/// FSST16 symbol table: up to 65,535 symbols with 16-bit codes.
/// Codes <c>0..65534</c> are symbols; code 65,535 is the escape marker, followed in the compressed
/// stream by one literal byte.
/// </summary>
/// <remarks>
/// Tables produced by <see cref="Fsst16Encoder.BuildSymbolTable"/> always contain all 256
/// single-byte symbols, so the encoder never needs to escape. The escape path still exists for
/// tables that do not cover every byte — an empty table, or one imported from another writer.
/// </remarks>
public sealed class SymbolTable16
{
    /// <summary>
    /// Lossy hash table for length-3+ symbols. Sized for a ~0.5 load factor at the 65,535-symbol
    /// ceiling; real tables are far smaller, so most inserts land in a free slot.
    /// </summary>
    internal const int HashTabSize = 1 << 17;

    /// <summary>Escape code, and the "no symbol here" sentinel in the code lookup tables.</summary>
    internal const int EscCode = Symbol16.EscCode;

    internal const int MaxSymbols = Symbol16.MaxSymbols;

    /// <summary>2-byte prefix lookup: code of the length-2 symbol, or <see cref="EscCode"/> if none.</summary>
    internal readonly ushort[] ShortCodes = new ushort[65536];

    /// <summary>Single-byte lookup: code of the length-1 symbol, or <see cref="EscCode"/> if the byte is uncovered.</summary>
    internal readonly ushort[] ByteCodes = new ushort[256];

    /// <summary>Symbols indexed by code. Slot <see cref="EscCode"/> is a length-1 placeholder so the encoder advances one byte on escape.</summary>
    internal readonly Symbol16[] Symbols = new Symbol16[Symbol16.CodeMax];

    /// <summary>Lossy hash table for length-3+ symbols.</summary>
    internal readonly Symbol16[] HashTab = new Symbol16[HashTabSize];

    /// <summary>Count of symbols per length: index 0 is length 1, index 15 is length 16.</summary>
    internal readonly int[] LenHisto = new int[Symbol16.MaxLength];

    internal int NSymbols;

    /// <summary>
    /// Number of symbols in this table, in <c>[0, 65535]</c>. They occupy codes
    /// <c>0 .. SymbolCount - 1</c>; code 65,535 is reserved as the escape code and is never counted.
    /// </summary>
    public int SymbolCount => NSymbols;

    /// <summary>
    /// Longest symbol this table may hold, in <c>[1, 16]</c>. The Parquet FSST proposal is
    /// self-contradictory on the FSST_16 cap — §1.2 says 8 bytes while §3.3, §3.5 and §3.6 all imply
    /// 16 — so the limit is chosen at training time rather than baked in. See
    /// <see cref="Fsst16Encoder.BuildSymbolTable"/>.
    /// </summary>
    public int MaxSymbolLength { get; }

    internal SymbolTable16(int maxSymbolLength)
    {
        MaxSymbolLength = maxSymbolLength;
        NSymbols = 0;

        var unused = Symbol16.Free();
        for (int i = 0; i < Symbol16.CodeMax; i++)
            Symbols[i] = unused;

        // The escape "symbol" consumes exactly one input byte.
        var esc = Symbol16.FromByte(0);
        esc.SetCodeLen(EscCode, 1);
        Symbols[EscCode] = esc;

        for (int i = 0; i < HashTabSize; i++)
            HashTab[i] = unused;

        for (int i = 0; i < 256; i++)
            ByteCodes[i] = EscCode;

        for (int i = 0; i < 65536; i++)
            ShortCodes[i] = EscCode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HashInsert(in Symbol16 s)
    {
        int idx = (int)(s.Hash() & (HashTabSize - 1));
        if (!HashTab[idx].IsFree())
            return false; // slot taken

        int len = s.Length();
        HashTab[idx].Icl = s.Icl;
        HashTab[idx].Lo = s.Lo & Symbol16.LoMask[len];
        HashTab[idx].Hi = s.Hi & Symbol16.HiMask[len];
        return true;
    }

    /// <summary>
    /// Assign the next free code to <paramref name="s"/> and index it. Returns false when the table
    /// is full, the symbol is too long, the symbol duplicates one already present, or the lossy hash
    /// slot it needs is taken.
    /// </summary>
    internal bool Add(Symbol16 s)
    {
        if (NSymbols >= MaxSymbols)
            return false;

        int len = s.Length();
        if (len < 1 || len > MaxSymbolLength)
            return false;

        int code = NSymbols;
        s.SetCodeLen(code, len);

        if (len == 1)
        {
            if (ByteCodes[s.First()] != EscCode)
                return false; // byte already covered
            ByteCodes[s.First()] = (ushort)code;
        }
        else if (len == 2)
        {
            if (ShortCodes[s.First2()] != EscCode)
                return false; // 2-byte prefix already covered
            ShortCodes[s.First2()] = (ushort)code;
        }
        else if (!HashInsert(s))
        {
            return false;
        }

        Symbols[code] = s;
        LenHisto[len - 1]++;
        NSymbols++;
        return true;
    }

    /// <summary>
    /// Greedy longest-match lookup: 3+ byte symbols via the lossy hash, then 2-byte, then 1-byte.
    /// Returns <see cref="EscCode"/> when the leading byte has no symbol at all.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int FindLongestSymbol(in Symbol16 s)
    {
        int idx = (int)(s.Hash() & (HashTabSize - 1));
        ref Symbol16 h = ref HashTab[idx];
        if (h.Icl <= s.Icl)
        {
            int hlen = h.Length();
            if (h.Lo == (s.Lo & Symbol16.LoMask[hlen]) && h.Hi == (s.Hi & Symbol16.HiMask[hlen]))
                return h.Code();
        }

        if (s.Length() >= 2)
        {
            ushort code = ShortCodes[s.First2()];
            if (code != EscCode)
                return code;
        }

        return ByteCodes[s.First()];
    }

    /// <summary>
    /// Renumber codes so symbol lengths ascend with code. Called once after training: the Parquet
    /// FSST symbol-table page derives codes from a per-length histogram, so emitting them in
    /// ascending-length order lets a consumer use these codes verbatim instead of remapping every
    /// code in the compressed stream.
    /// </summary>
    internal void SortCodesByLength()
    {
        if (NSymbols == 0)
            return;

        Span<int> next = stackalloc int[Symbol16.MaxLength + 1];
        int running = 0;
        for (int len = 1; len <= Symbol16.MaxLength; len++)
        {
            next[len] = running;
            running += LenHisto[len - 1];
        }

        var newCode = new int[NSymbols];
        for (int code = 0; code < NSymbols; code++)
            newCode[code] = next[Symbols[code].Length()]++;

        var reordered = new Symbol16[NSymbols];
        for (int code = 0; code < NSymbols; code++)
        {
            var s = Symbols[code];
            s.SetCodeLen(newCode[code], s.Length());
            reordered[newCode[code]] = s;
        }
        Array.Copy(reordered, Symbols, NSymbols);

        for (int i = 0; i < 256; i++)
        {
            if (ByteCodes[i] != EscCode)
                ByteCodes[i] = (ushort)newCode[ByteCodes[i]];
        }

        for (int i = 0; i < 65536; i++)
        {
            if (ShortCodes[i] != EscCode)
                ShortCodes[i] = (ushort)newCode[ShortCodes[i]];
        }

        for (int i = 0; i < HashTabSize; i++)
        {
            if (!HashTab[i].IsFree())
                HashTab[i] = Symbols[newCode[HashTab[i].Code()]];
        }
    }

    /// <summary>
    /// Writes the symbols in the framing-agnostic layout consumed by
    /// <see cref="Fsst16Decoder.FromSymbols"/>: <paramref name="lengths"/> receives
    /// <see cref="SymbolCount"/> bytes (one length in <c>[1, MaxSymbolLength]</c> per code, in code
    /// order), <paramref name="packedValues"/> receives <c>SymbolCount * 16</c> bytes (each symbol's
    /// bytes packed little-endian, zero-padded to 16). Lengths are non-decreasing, so a consumer can
    /// build a per-length histogram from this output directly.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="lengths"/> is shorter than <see cref="SymbolCount"/>, or
    /// <paramref name="packedValues"/> is shorter than <c>SymbolCount * 16</c>.
    /// </exception>
    public void ExportRaw(Span<byte> lengths, Span<byte> packedValues)
    {
        if (lengths.Length < NSymbols)
            throw new ArgumentException($"Buffer must be at least {NSymbols} bytes (SymbolCount).", nameof(lengths));
        if (packedValues.Length < NSymbols * 16)
            throw new ArgumentException($"Buffer must be at least {NSymbols * 16} bytes (SymbolCount * 16).", nameof(packedValues));

        for (int i = 0; i < NSymbols; i++)
        {
            var sym = Symbols[i];
            lengths[i] = (byte)sym.Length();
            BinaryPrimitives.WriteUInt64LittleEndian(packedValues.Slice(i * 16, 8), sym.Lo);
            BinaryPrimitives.WriteUInt64LittleEndian(packedValues.Slice(i * 16 + 8, 8), sym.Hi);
        }
    }
}
