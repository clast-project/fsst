// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST16 encoder: builds a 16-bit symbol table and compresses using 2-byte little-endian codes.
/// Code 65,535 is the escape marker and is followed by the literal byte as a little-endian
/// <c>uint16</c> in <c>[0, 255]</c>.
/// </summary>
public static class Fsst16Encoder
{
    private const int SampleTarget = 1 << 18; // 256KB — a 16-bit table needs more evidence than FSST8/12
    private const int SampleMaxSize = 2 * SampleTarget;
    private const int SampleChunk = 512;

    /// <summary>Slots left for multi-byte symbols once all 256 single-byte symbols are reserved.</summary>
    private const int MaxMultiByteSymbols = Symbol16.MaxSymbols - 256;

    /// <summary>
    /// Build a 16-bit symbol table from a representative corpus. The result always contains all 256
    /// single-byte symbols — with 65,535 codes available there is no reason to leave a byte to a
    /// 4-byte escape — so tables from this method never escape and never exceed 2x expansion.
    /// </summary>
    /// <param name="rows">Sample rows. Only used for training; they need not be the full corpus.</param>
    /// <param name="maxSymbolLength">
    /// Longest symbol the table may contain, in <c>[1, 16]</c>. The Parquet FSST proposal contradicts
    /// itself here — §1.2 says symbols are 1-8 bytes while §3.3, §3.5 and §3.6 all describe a 16-byte
    /// cap — so pass 8 to stay valid under the stricter reading and 16 (the default) for the larger one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSymbolLength"/> is outside <c>[1, 16]</c>.</exception>
    public static SymbolTable16 BuildSymbolTable(ReadOnlySpan<byte[]> rows, int maxSymbolLength = 16)
    {
        if (maxSymbolLength < 1 || maxSymbolLength > Symbol16.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maxSymbolLength), maxSymbolLength, "maxSymbolLength must be in [1, 16].");

        // Progressive sampling, keeping whichever table compresses its own sample best.
        ReadOnlySpan<int> sampleFracs = [14, 52, 90, 128];

        var bestTable = NewBaseTable(maxSymbolLength);
        long bestGain = long.MinValue;

        for (int iter = 0; iter < sampleFracs.Length; iter++)
        {
            byte[] sample = MakeSample(rows, sampleFracs[iter]);
            if (sample.Length == 0)
                continue;

            var newTable = BuildFromSample(sample, bestTable, sampleFracs[iter], maxSymbolLength);
            long newGain = EvaluateGain(sample, newTable);

            if (newGain > bestGain)
            {
                bestGain = newGain;
                bestTable = newTable;
            }
        }

        bestTable.SortCodesByLength();
        return bestTable;
    }

    /// <summary>A table holding nothing but the 256 single-byte symbols, so no byte ever escapes.</summary>
    private static SymbolTable16 NewBaseTable(int maxSymbolLength)
    {
        var table = new SymbolTable16(maxSymbolLength);
        for (int i = 0; i < 256; i++)
            table.Add(Symbol16.FromByte((byte)i));
        return table;
    }

    internal static byte[] MakeSample(ReadOnlySpan<byte[]> rows, int sampleFrac)
    {
        long totalLen = 0;
        for (int i = 0; i < rows.Length; i++)
            totalLen += rows[i].Length;

        if (totalLen == 0) return [];

        if (totalLen <= SampleTarget)
        {
            var all = new byte[totalLen];
            int pos = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].CopyTo(all.AsSpan(pos));
                pos += rows[i].Length;
            }
            return all;
        }

        // Sample proportionally into a single pre-sized buffer.
        var buffer = new byte[SampleMaxSize];
        int written = 0;
        int hash = 0;
        for (int i = 0; i < rows.Length && written < SampleMaxSize; i++)
        {
            var s = rows[i];
            for (int j = 0; j < s.Length && written < SampleMaxSize; j += SampleChunk)
            {
                hash = (hash * 1103515245 + 12345) & 0x7FFFFFFF;
                if ((hash & 127) < sampleFrac)
                {
                    int chunkLen = Math.Min(SampleChunk, Math.Min(s.Length - j, SampleMaxSize - written));
                    s.AsSpan(j, chunkLen).CopyTo(buffer.AsSpan(written));
                    written += chunkLen;
                }
            }
        }

        // Fallback: if random sampling produced nothing, take from the start.
        if (written == 0)
        {
            for (int i = 0; i < rows.Length && written < SampleTarget; i++)
            {
                int take = Math.Min(rows[i].Length, SampleTarget - written);
                rows[i].AsSpan(0, take).CopyTo(buffer.AsSpan(written));
                written += take;
            }
        }

        if (written == 0) return [];
        if (written == buffer.Length) return buffer;
        var result = new byte[written];
        Buffer.BlockCopy(buffer, 0, result, 0, written);
        return result;
    }

    private static unsafe SymbolTable16 BuildFromSample(
        byte[] sample, SymbolTable16 prevTable, int sampleFrac, int maxSymbolLength)
    {
        int threshold = 5 * sampleFrac / 128;
        if (threshold < 1) threshold = 1;

        // Frequencies of the symbols the previous table picks, and of adjacent pairs.
        // FSST8's dense Counters cannot be reused here: a 65,536-square pair matrix is not viable,
        // so this follows the sparse FSST12 approach.
        var count1 = new int[Symbol16.CodeMax];
        var count2 = new Dictionary<(int, int), int>();

        fixed (byte* samplePtr = sample)
        {
            byte* cur = samplePtr;
            byte* end = samplePtr + sample.Length;
            int prevCode = -1;

            while (cur < end)
            {
                var sym = Symbol16.FromPointer(cur, (int)(end - cur));
                int code = prevTable.FindLongestSymbol(sym);
                int len = prevTable.Symbols[code].Length();

                count1[code]++;
                if (prevCode >= 0)
                {
                    var key = (prevCode, code);
                    count2.TryGetValue(key, out int c);
                    count2[key] = c + 1;
                }

                prevCode = code;
                cur += len;
            }
        }

        // Score candidates. A code always costs 2 bytes, so a length-L symbol saves L-1 codes --
        // 2*(L-1) bytes -- against spelling those bytes out one code at a time. Scores are only
        // used to rank candidates against each other, so the constant factor of 2 is dropped.
        var candidates = new Dictionary<(ulong Lo, ulong Hi, int Len), (Symbol16 Symbol, long Gain)>();

        void AddOrInc(Symbol16 s, long count)
        {
            int len = s.Length();
            if (len < 2) return; // single bytes are reserved unconditionally, not scored

            var key = (s.Lo, s.Hi, len);
            long gain = count * (len - 1);
            if (candidates.TryGetValue(key, out var existing))
                candidates[key] = (s, existing.Gain + gain);
            else
                candidates[key] = (s, gain);
        }

        for (int code = 0; code < Symbol16.CodeMax; code++)
        {
            int count = count1[code];
            if (count < threshold || code == SymbolTable16.EscCode) continue;
            AddOrInc(prevTable.Symbols[code], count);
        }

        foreach (var pair in count2)
        {
            if (pair.Value < threshold) continue;

            int c1 = pair.Key.Item1;
            int c2 = pair.Key.Item2;
            if (c1 == SymbolTable16.EscCode || c2 == SymbolTable16.EscCode) continue;

            var s1 = prevTable.Symbols[c1];
            if (s1.Length() >= maxSymbolLength) continue;

            AddOrInc(Symbol16.Concat(s1, prevTable.Symbols[c2], maxSymbolLength), pair.Value);
        }

        var sorted = new List<(Symbol16 Symbol, long Gain)>(candidates.Values);
        sorted.Sort(CompareCandidates);

        var newTable = NewBaseTable(maxSymbolLength);
        int added = 0;
        for (int i = 0; i < sorted.Count && added < MaxMultiByteSymbols; i++)
        {
            if (newTable.Add(sorted[i].Symbol))
                added++;
        }

        return newTable;
    }

    /// <summary>Highest gain first, with a total order on ties so table building is deterministic.</summary>
    private static int CompareCandidates((Symbol16 Symbol, long Gain) a, (Symbol16 Symbol, long Gain) b)
    {
        if (a.Gain != b.Gain) return b.Gain.CompareTo(a.Gain);
        int lenA = a.Symbol.Length();
        int lenB = b.Symbol.Length();
        if (lenA != lenB) return lenB.CompareTo(lenA);
        if (a.Symbol.Hi != b.Symbol.Hi) return a.Symbol.Hi.CompareTo(b.Symbol.Hi);
        return a.Symbol.Lo.CompareTo(b.Symbol.Lo);
    }

    /// <summary>Bytes saved by compressing <paramref name="sample"/> with <paramref name="table"/>.</summary>
    private static unsafe long EvaluateGain(byte[] sample, SymbolTable16 table)
    {
        long compressedSize = 0;

        fixed (byte* samplePtr = sample)
        {
            byte* cur = samplePtr;
            byte* end = samplePtr + sample.Length;

            while (cur < end)
            {
                var sym = Symbol16.FromPointer(cur, (int)(end - cur));
                int code = table.FindLongestSymbol(sym);

                // 2 bytes per code; an escape costs 4, being the marker plus a uint16 literal.
                // Unreachable while every candidate table covers all 256 single bytes, but the cost
                // model should not quietly disagree with what TryCompress emits.
                compressedSize += code == SymbolTable16.EscCode ? 4 : 2;
                cur += table.Symbols[code].Length();
            }
        }

        return sample.Length - compressedSize;
    }

    /// <summary>
    /// Returns an upper bound on the number of bytes <see cref="Compress(SymbolTable16, ReadOnlySpan{byte})"/>
    /// may produce for an input of the given length.
    /// </summary>
    /// <remarks>
    /// The bound is 4x: every input byte escaping to a 2-byte marker plus a 2-byte literal. Tables
    /// from <see cref="BuildSymbolTable"/> cover all 256 bytes and never escape, so they stay at or
    /// under 2x; the 4x bound only binds for a table that leaves bytes uncovered.
    /// </remarks>
    public static int MaxCompressedLength(int inputLength)
    {
        if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        long max = (long)inputLength * 4;
        if (max > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(inputLength), "Input is too large.");
        return (int)max;
    }

    /// <summary>
    /// Compress <paramref name="input"/> into <paramref name="destination"/> using 16-bit codes.
    /// Each code is written little-endian; code 65,535 marks an escape and is followed by the
    /// literal byte as a little-endian <c>uint16</c> in <c>[0, 255]</c>. Returns false (and sets
    /// <paramref name="written"/> to 0) if <paramref name="destination"/> is too small.
    /// </summary>
    public static unsafe bool TryCompress(
        SymbolTable16 table, ReadOnlySpan<byte> input, Span<byte> destination, out int written)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        written = 0;
        if (input.Length == 0) return true;

        int outPos = 0;
        int dstLen = destination.Length;

        fixed (byte* inPtr = input)
        fixed (byte* dstPtr = destination)
        {
            byte* cur = inPtr;
            byte* end = inPtr + input.Length;

            while (cur < end)
            {
                var sym = Symbol16.FromPointer(cur, (int)(end - cur));
                int code = table.FindLongestSymbol(sym);

                if (code == SymbolTable16.EscCode)
                {
                    // The escape marker is followed by the literal as a little-endian uint16 whose
                    // value is 0..255, not by a bare byte: §8.3 reads it with read_uint16_le. Keeping
                    // it two bytes is also what makes every stream an even number of bytes, which
                    // the same section requires.
                    if (outPos + 4 > dstLen) return false;
                    dstPtr[outPos] = 0xFF;
                    dstPtr[outPos + 1] = 0xFF;
                    dstPtr[outPos + 2] = *cur;
                    dstPtr[outPos + 3] = 0;
                    outPos += 4;
                    cur++;
                }
                else
                {
                    if (outPos + 2 > dstLen) return false;
                    dstPtr[outPos] = (byte)code;
                    dstPtr[outPos + 1] = (byte)(code >> 8);
                    outPos += 2;
                    cur += table.Symbols[code].Length();
                }
            }
        }

        written = outPos;
        return true;
    }

    /// <summary>Compress <paramref name="input"/> and append the result to <paramref name="writer"/>.</summary>
    public static void Compress(SymbolTable16 table, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (input.Length == 0) return;

        int max = MaxCompressedLength(input.Length);
        Span<byte> dst = writer.GetSpan(max);
        if (!TryCompress(table, input, dst, out int written))
            throw new InvalidOperationException("Buffer writer returned a span smaller than the size hint.");
        writer.Advance(written);
    }

    /// <summary>Compress a UTF-8 encoded string using 16-bit codes.</summary>
    public static byte[] Compress(SymbolTable16 table, string input)
    {
        if (string.IsNullOrEmpty(input)) return [];
        int byteCount = Encoding.UTF8.GetByteCount(input);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int actual = Encoding.UTF8.GetBytes(input, 0, input.Length, rented, 0);
            return Compress(table, rented.AsSpan(0, actual));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Compress input bytes using 16-bit codes.</summary>
    public static byte[] Compress(SymbolTable16 table, ReadOnlySpan<byte> input)
    {
        if (input.Length == 0) return [];

        int max = MaxCompressedLength(input.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            if (!TryCompress(table, input, rented.AsSpan(0, max), out int written))
                throw new InvalidOperationException("MaxCompressedLength was too small.");
            var result = new byte[written];
            Array.Copy(rented, result, written);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Compress multiple rows, returning compressed bytes and per-row lengths.</summary>
    public static (byte[] compressedData, int[] lengths) CompressBatch(
        SymbolTable16 table, ReadOnlySpan<byte[]> rows)
    {
        var lengths = new int[rows.Length];

        long maxTotal = 0;
        for (int i = 0; i < rows.Length; i++)
            maxTotal += MaxCompressedLength(rows[i].Length);
        if (maxTotal > int.MaxValue)
            throw new ArgumentException("Batch worst-case size exceeds Int32.MaxValue.", nameof(rows));
        if (maxTotal == 0)
            return ([], lengths);

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)maxTotal);
        try
        {
            int totalWritten = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                int slot = MaxCompressedLength(rows[i].Length);
                if (!TryCompress(table, rows[i], rented.AsSpan(totalWritten, slot), out int written))
                    throw new InvalidOperationException("MaxCompressedLength was too small.");
                lengths[i] = written;
                totalWritten += written;
            }
            var result = new byte[totalWritten];
            Array.Copy(rented, result, totalWritten);
            return (result, lengths);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
