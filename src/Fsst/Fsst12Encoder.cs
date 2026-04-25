// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST12 encoder: builds a 12-bit symbol table and compresses using 1.5-byte codes.
/// No escape mechanism — all 4096 codes are valid. Two 12-bit codes pack into 3 bytes.
/// </summary>
public static class Fsst12Encoder
{
    private const int SampleTarget = 1 << 17; // 128KB for FSST12
    private const int SampleMaxSize = 2 * SampleTarget;
    private const int SampleChunk = 512;
    private const int MaxSymbols = 4096 - 256; // 3840 real symbols beyond base

    /// <summary>Build a 12-bit symbol table from input strings.</summary>
    public static SymbolMap BuildSymbolTable(ReadOnlySpan<byte[]> strings)
    {
        if (strings.Length == 0)
            return new SymbolMap();

        // 4 iterations with progressive sampling
        ReadOnlySpan<int> sampleFracs = [14, 52, 90, 128];

        var bestTable = new SymbolMap();
        long bestGain = 0;

        for (int iter = 0; iter < 4; iter++)
        {
            byte[] sample = MakeSample(strings, sampleFracs[iter]);
            if (sample.Length == 0)
                continue;

            // Build new table from sample
            var newTable = BuildFromSample(sample, bestTable, sampleFracs[iter]);

            // Evaluate gain
            long newGain = EvaluateGain(sample, newTable);

            if (newGain > bestGain)
            {
                bestGain = newGain;
                bestTable = newTable;
            }
        }

        return bestTable;
    }

    internal static byte[] MakeSample(ReadOnlySpan<byte[]> strings, int sampleFrac)
    {
        long totalLen = 0;
        for (int i = 0; i < strings.Length; i++)
            totalLen += strings[i].Length;

        if (totalLen == 0) return [];

        if (totalLen <= SampleTarget)
        {
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

        // Fallback: if random sampling produced nothing, take from the start.
        if (written == 0)
        {
            for (int i = 0; i < strings.Length && written < SampleTarget; i++)
            {
                int take = Math.Min(strings[i].Length, SampleTarget - written);
                strings[i].AsSpan(0, take).CopyTo(buffer.AsSpan(written));
                written += take;
            }
        }

        if (written == 0) return [];
        if (written == buffer.Length) return buffer;
        var result = new byte[written];
        Buffer.BlockCopy(buffer, 0, result, 0, written);
        return result;
    }

    private static unsafe SymbolMap BuildFromSample(byte[] sample, SymbolMap prevTable, int sampleFrac)
    {
        int threshold = 5 * sampleFrac / 128;
        if (threshold < 1) threshold = 1;

        // Count symbol and pair frequencies
        var count1 = new int[SymbolMap.CodeMax12];
        var count2 = new Dictionary<(int, int), int>();

        fixed (byte* samplePtr = sample)
        {
            byte* cur = samplePtr;
            byte* end = samplePtr + sample.Length;
            int prevCode = -1;

            while (cur < end)
            {
                int avail = (int)(end - cur);
                var sym = Symbol.FromPointer(cur, Math.Min(avail, 8));
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

        // Score candidates
        var candidates = new List<(Symbol symbol, long gain)>();

        // Single symbols
        for (int i = 0; i < SymbolMap.CodeMax12; i++)
        {
            if (count1[i] < threshold) continue;
            var sym = prevTable.Symbols[i];
            int len = sym.Length();
            // Gain formula for 12-bit: 2*bytesConsumed - 3 (1.5 byte code cost)
            long gain = (long)count1[i] * (2 * len - 3);
            if (gain > 0)
                candidates.Add((sym, gain));
        }

        // Pair symbols (concatenations)
        foreach (var ((c1, c2), count) in count2)
        {
            if (count < threshold) continue;
            var s1 = prevTable.Symbols[c1];
            var s2 = prevTable.Symbols[c2];
            int combinedLen = s1.Length() + s2.Length();
            if (combinedLen > Symbol.MaxLength) continue;

            var concat = Symbol.Concat(s1, s2);
            long gain = (long)count * (2 * combinedLen - 3);
            if (gain > 0)
                candidates.Add((concat, gain));
        }

        candidates.Sort((a, b) => b.gain.CompareTo(a.gain));

        var newTable = new SymbolMap();
        int added = 0;
        for (int i = 0; i < candidates.Count && added < MaxSymbols; i++)
        {
            if (newTable.Add(candidates[i].symbol))
                added++;
        }

        return newTable;
    }

    private static unsafe long EvaluateGain(byte[] sample, SymbolMap table)
    {
        long totalSymLen = 0;
        int codeCount = 0;

        fixed (byte* samplePtr = sample)
        {
            byte* cur = samplePtr;
            byte* end = samplePtr + sample.Length;

            while (cur < end)
            {
                int avail = (int)(end - cur);
                var sym = Symbol.FromPointer(cur, Math.Min(avail, 8));
                int code = table.FindLongestSymbol(sym);
                int len = table.Symbols[code].Length();

                totalSymLen += len;
                codeCount++;
                cur += len;
            }
        }

        // FSST12 compresses to 1.5 bytes per code
        // Gain = original bytes - compressed bytes
        long compressedSize = (codeCount * 3 + 1) / 2; // 1.5 bytes per code, rounded up
        return sample.Length - compressedSize;
    }

    /// <summary>
    /// Returns an upper bound on the number of bytes <see cref="Compress"/> may produce
    /// for an input of the given length.
    /// </summary>
    public static int MaxCompressedLength(int inputLength)
    {
        if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        // Worst case: every byte produces one 12-bit code, packed at 1.5 bytes per code (rounded up).
        long max = ((long)inputLength * 3 + 1) / 2;
        if (max > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(inputLength), "Input is too large.");
        return (int)max;
    }

    /// <summary>
    /// Compress <paramref name="input"/> into <paramref name="destination"/> using 12-bit codes.
    /// Two codes pack into 3 bytes: [low8(c1)] [high4(c1) | low4(c2) &lt;&lt; 4] [c2 &gt;&gt; 4].
    /// Returns false (and sets <paramref name="written"/> to 0) if <paramref name="destination"/>
    /// is too small.
    /// </summary>
    public static unsafe bool TryCompress(
        SymbolMap table, ReadOnlySpan<byte> input, Span<byte> destination, out int written)
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

            byte pending = 0;
            bool hasPending = false;

            while (cur < end)
            {
                int avail = (int)(end - cur);
                var sym = Symbol.FromPointer(cur, Math.Min(avail, 8));
                int code = table.FindLongestSymbol(sym);
                int len = table.Symbols[code].Length();
                cur += len;

                if (!hasPending)
                {
                    if (outPos >= dstLen) return false;
                    dstPtr[outPos++] = (byte)(code & 0xFF);
                    pending = (byte)((code >> 8) & 0x0F);
                    hasPending = true;
                }
                else
                {
                    if (outPos + 2 > dstLen) return false;
                    dstPtr[outPos++] = (byte)(pending | ((code & 0x0F) << 4));
                    dstPtr[outPos++] = (byte)(code >> 4);
                    hasPending = false;
                }
            }

            if (hasPending)
            {
                if (outPos >= dstLen) return false;
                dstPtr[outPos++] = pending;
            }
        }

        written = outPos;
        return true;
    }

    /// <summary>Compress <paramref name="input"/> and append the result to <paramref name="writer"/>.</summary>
    public static void Compress(SymbolMap table, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (input.Length == 0) return;

        int max = MaxCompressedLength(input.Length);
        Span<byte> dst = writer.GetSpan(max);
        if (!TryCompress(table, input, dst, out int written))
            throw new InvalidOperationException("Buffer writer returned a span smaller than the size hint.");
        writer.Advance(written);
    }

    /// <summary>Compress a UTF-8 encoded string using 12-bit codes.</summary>
    public static byte[] Compress(SymbolMap table, string input)
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

    /// <summary>Compress input bytes using 12-bit codes.</summary>
    public static byte[] Compress(SymbolMap table, ReadOnlySpan<byte> input)
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

    /// <summary>Compress multiple strings, returning compressed bytes and per-string lengths.</summary>
    public static (byte[] compressedData, int[] lengths) CompressBatch(
        SymbolMap table, ReadOnlySpan<byte[]> strings)
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
