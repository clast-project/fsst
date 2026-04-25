// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST8 encoder: builds a symbol table from input data and compresses using it.
/// Uses escape code 255 for literal bytes that aren't in the symbol table.
/// </summary>
public static class FsstEncoder
{
    private const int SampleTarget = 1 << 14; // 16KB
    private const int SampleMaxSize = 2 * SampleTarget;
    private const int SampleChunk = 512;
    private const int MaxSymbols = 255;
    private const int EscCode = 255;

    /// <summary>Build a symbol table from input strings.</summary>
    public static SymbolTable BuildSymbolTable(ReadOnlySpan<byte[]> strings, bool zeroTerminated = false)
    {
        if (strings.Length == 0)
        {
            var empty = new SymbolTable();
            empty.Finalize(zeroTerminated);
            return empty;
        }

        // Collect all data as a single sample (matching C++ makeSample for small inputs)
        byte[] sample = MakeSample(strings, 128);
        if (sample.Length == 0)
        {
            var empty = new SymbolTable();
            empty.Finalize(zeroTerminated);
            return empty;
        }

        // C++ pattern: iterate with progressive sampling, keeping best table by gain
        var st = new SymbolTable();
        st.ZeroTerminated = zeroTerminated;
        var bestTable = new SymbolTable();
        bestTable.ZeroTerminated = zeroTerminated;
        long bestGain = -SampleMaxSize;

        var counters = new Counters();
        byte[] bestCount1High = new byte[Symbol.CodeMax];
        byte[] bestCount1Low = new byte[Symbol.CodeMax];

        int sampleFrac = 8;
        while (true)
        {
            counters.Clear();
            long gain = CompressCount(sample, st, counters, sampleFrac);

            if (gain >= bestGain)
            {
                // Backup counters
                Array.Copy(counters.Count1High, bestCount1High, Symbol.CodeMax);
                Array.Copy(counters.Count1Low, bestCount1Low, Symbol.CodeMax);
                CopyTable(st, bestTable);
                bestGain = gain;
            }

            if (sampleFrac >= 128) break;

            MakeTable(counters, st, sampleFrac);
            sampleFrac += 30;
        }

        // Restore best counters and rebuild table
        Array.Copy(bestCount1High, counters.Count1High, Symbol.CodeMax);
        Array.Copy(bestCount1Low, counters.Count1Low, Symbol.CodeMax);
        MakeTable(counters, bestTable, 128);

        bestTable.Finalize(zeroTerminated);
        return bestTable;
    }

    private static void CopyTable(SymbolTable src, SymbolTable dst)
    {
        Array.Copy(src.ShortCodes, dst.ShortCodes, src.ShortCodes.Length);
        Array.Copy(src.ByteCodes, dst.ByteCodes, src.ByteCodes.Length);
        Array.Copy(src.Symbols, dst.Symbols, src.Symbols.Length);
        Array.Copy(src.HashTab, dst.HashTab, src.HashTab.Length);
        dst.NSymbols = src.NSymbols;
        dst.SuffixLim = src.SuffixLim;
        dst.Terminator = src.Terminator;
        dst.ZeroTerminated = src.ZeroTerminated;
        Array.Copy(src.LenHisto, dst.LenHisto, src.LenHisto.Length);
    }

    /// <summary>Randomly sample ~16KB from input strings in 512-byte chunks.</summary>
    internal static byte[] MakeSample(ReadOnlySpan<byte[]> strings, int sampleFrac)
    {
        long totalLen = 0;
        for (int i = 0; i < strings.Length; i++)
            totalLen += strings[i].Length;

        if (totalLen == 0)
            return [];

        if (totalLen <= SampleTarget)
        {
            // Use all data
            var all = new byte[totalLen];
            int pos = 0;
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i].CopyTo(all.AsSpan(pos));
                pos += strings[i].Length;
            }
            return all;
        }

        // Sample proportionally into a single pre-sized buffer.
        var buffer = new byte[SampleMaxSize];
        int written = 0;
        int hash = 0;
        for (int i = 0; i < strings.Length && written < SampleMaxSize; i++)
        {
            var s = strings[i];
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

        if (written == 0) return MakeSampleFallback(strings);
        if (written == buffer.Length) return buffer;
        var result = new byte[written];
        Buffer.BlockCopy(buffer, 0, result, 0, written);
        return result;
    }

    private static byte[] MakeSampleFallback(ReadOnlySpan<byte[]> strings)
    {
        // If sampling produced nothing, take from the start.
        var buffer = new byte[SampleTarget];
        int written = 0;
        for (int i = 0; i < strings.Length && written < SampleTarget; i++)
        {
            int take = Math.Min(strings[i].Length, SampleTarget - written);
            strings[i].AsSpan(0, take).CopyTo(buffer.AsSpan(written));
            written += take;
        }
        if (written == 0) return [];
        if (written == buffer.Length) return buffer;
        var result = new byte[written];
        Buffer.BlockCopy(buffer, 0, result, 0, written);
        return result;
    }

    /// <summary>
    /// Compress sample data with the current table, counting symbol frequencies.
    /// Returns the compression gain (bytes saved vs escape-every-byte).
    /// C++ formula: gain += bytesConsumed - (1 + isEscapeCode(code))
    /// </summary>
    internal static unsafe long CompressCount(byte[] sample, SymbolTable table, Counters counters, int sampleFrac = 128)
    {
        long gain = 0;

        fixed (byte* samplePtr = sample)
        {
            byte* cur = samplePtr;
            byte* end = samplePtr + sample.Length;

            if (cur < end)
            {
                int code1 = table.FindLongestSymbol(Symbol.FromPointer(cur, Math.Min((int)(end - cur), 8)));
                int len1 = table.Symbols[code1].Length();
                byte* start = cur;
                cur += len1;
                // gain: bytesConsumed - cost. Cost is 1 for real symbol, 2 for escape.
                bool isEscape1 = code1 < Symbol.CodeBase;
                gain += len1 - (1 + (isEscape1 ? 1 : 0));

                while (cur < end)
                {
                    counters.Count1Inc(code1);
                    // Also count the first byte if multi-byte symbol
                    if (table.Symbols[code1].Length() != 1)
                        counters.Count1Inc(*start);

                    start = cur;
                    int code2 = table.FindLongestSymbol(Symbol.FromPointer(cur, Math.Min((int)(end - cur), 8)));
                    int len2 = table.Symbols[code2].Length();
                    cur += len2;

                    bool isEscape2 = code2 < Symbol.CodeBase;
                    gain += len2 - (1 + (isEscape2 ? 1 : 0));

                    if (sampleFrac < 128)
                    {
                        counters.Count2Inc(code1, code2);
                        if (len2 > 1)
                            counters.Count2Inc(code1, *start);
                    }

                    code1 = code2;
                }
                // Count the last symbol
                counters.Count1Inc(code1);
                if (table.Symbols[code1].Length() != 1)
                    counters.Count1Inc(*start);
            }
        }

        return gain;
    }

    /// <summary>Build a new symbol table from frequency counts, modifying st in-place.</summary>
    internal static void MakeTable(Counters counters, SymbolTable st, int sampleFrac)
    {
        int threshold = (5 * sampleFrac) / 128;
        if (threshold < 1) threshold = 1;

        // Collect candidate symbols with gain scores using a dictionary for dedup
        var candidates = new Dictionary<ulong, (Symbol symbol, long gain)>();

        void AddOrInc(Symbol s, long count)
        {
            if (count < threshold) return;
            long g = count * s.Length();
            ulong key = s.Val ^ ((ulong)s.Length() << 56);
            if (candidates.TryGetValue(key, out var existing))
                candidates[key] = (s, existing.gain + g);
            else
                candidates[key] = (s, g);
        }

        int nSymMax = Symbol.CodeBase + st.NSymbols;

        // Score single symbols
        for (int pos1 = 0; pos1 < nSymMax;)
        {
            int count = counters.Count1GetNext(ref pos1);
            if (count == 0) break;

            var sym = st.Symbols[pos1];
            long weight = sym.Length() == 1 ? 8L : 1L;
            AddOrInc(sym, weight * count);

            // Skip pair generation for full-length symbols or first-byte-is-terminator
            if (sampleFrac >= 128 || sym.Length() == Symbol.MaxLength)
            {
                pos1++;
                continue;
            }

            for (int pos2 = 0; pos2 < nSymMax;)
            {
                int count2 = counters.Count2GetNext(pos1, ref pos2);
                if (count2 == 0) break;

                var s2 = st.Symbols[pos2];
                var concat = Symbol.Concat(sym, s2);
                if (concat.Length() <= Symbol.MaxLength)
                    AddOrInc(concat, count2);

                pos2++;
            }
            pos1++;
        }

        // Sort by gain descending, take top 255
        var sorted = new List<(Symbol symbol, long gain)>(candidates.Values);
        sorted.Sort((a, b) => b.gain != a.gain ? b.gain.CompareTo(a.gain) : a.symbol.Val.CompareTo(b.symbol.Val));

        st.Clear();
        int added = 0;
        for (int i = 0; i < sorted.Count && added < MaxSymbols; i++)
        {
            if (st.Add(sorted[i].symbol))
                added++;
        }
    }

    /// <summary>
    /// Returns an upper bound on the number of bytes <see cref="Compress"/> may produce
    /// for an input of the given length.
    /// </summary>
    public static int MaxCompressedLength(int inputLength)
    {
        if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        // Worst case: every input byte is escaped, producing 2 output bytes per input byte.
        return inputLength * 2;
    }

    /// <summary>
    /// Compress <paramref name="input"/> into <paramref name="destination"/>.
    /// Returns false (and sets <paramref name="written"/> to 0) if <paramref name="destination"/>
    /// is too small. Use <see cref="MaxCompressedLength"/> to size the destination safely.
    /// </summary>
    public static unsafe bool TryCompress(
        SymbolTable table, ReadOnlySpan<byte> input, Span<byte> destination, out int written)
    {
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
                int avail = (int)(end - cur);
                var sym = Symbol.FromPointer(cur, Math.Min(avail, 8));
                int code = table.FindLongestSymbol(sym);
                int len = table.Symbols[code].Length();

                byte byteCode = (byte)code;
                if (byteCode == EscCode)
                {
                    if (outPos + 2 > dstLen) return false;
                    dstPtr[outPos] = byteCode;
                    dstPtr[outPos + 1] = *cur;
                    outPos += 2;
                }
                else
                {
                    if (outPos >= dstLen) return false;
                    dstPtr[outPos++] = byteCode;
                }

                cur += len;
            }
        }

        written = outPos;
        return true;
    }

    /// <summary>Compress <paramref name="input"/> and append the result to <paramref name="writer"/>.</summary>
    public static void Compress(SymbolTable table, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (input.Length == 0) return;

        int max = MaxCompressedLength(input.Length);
        Span<byte> dst = writer.GetSpan(max);
        if (!TryCompress(table, input, dst, out int written))
            throw new InvalidOperationException("Buffer writer returned a span smaller than the size hint.");
        writer.Advance(written);
    }

    /// <summary>Compress a UTF-8 encoded string using the given symbol table.</summary>
    public static byte[] Compress(SymbolTable table, string input)
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

    /// <summary>Compress input bytes using the given symbol table.</summary>
    public static byte[] Compress(SymbolTable table, ReadOnlySpan<byte> input)
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

    /// <summary>Compress multiple strings, returning compressed bytes and lengths.</summary>
    public static (byte[] compressedData, int[] lengths) CompressBatch(
        SymbolTable table, ReadOnlySpan<byte[]> strings)
    {
        var lengths = new int[strings.Length];

        long maxTotal = 0;
        for (int i = 0; i < strings.Length; i++)
            maxTotal += MaxCompressedLength(strings[i].Length);
        if (maxTotal > int.MaxValue)
            throw new ArgumentException("Batch worst-case size exceeds Int32.MaxValue.", nameof(strings));
        if (maxTotal == 0)
            return ([], lengths);

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)maxTotal);
        try
        {
            int totalWritten = 0;
            for (int i = 0; i < strings.Length; i++)
            {
                int slot = MaxCompressedLength(strings[i].Length);
                if (!TryCompress(table, strings[i], rented.AsSpan(totalWritten, slot), out int written))
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
