using System.Buffers;
using System.Runtime.CompilerServices;

namespace Fsst;

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

        var result = new List<byte>();
        int hash = 0;
        for (int i = 0; i < strings.Length && result.Count < SampleMaxSize; i++)
        {
            var s = strings[i];
            for (int j = 0; j < s.Length && result.Count < SampleMaxSize; j += SampleChunk)
            {
                hash = (hash * 1103515245 + 12345) & 0x7FFFFFFF;
                if ((hash & 127) < sampleFrac)
                {
                    int chunkLen = Math.Min(SampleChunk, s.Length - j);
                    result.AddRange(s.AsSpan(j, chunkLen).ToArray());
                }
            }
        }

        if (result.Count == 0)
        {
            for (int i = 0; i < strings.Length && result.Count < SampleTarget; i++)
            {
                int take = Math.Min(strings[i].Length, SampleTarget - result.Count);
                result.AddRange(strings[i].AsSpan(0, take).ToArray());
            }
        }

        return [.. result];
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
    /// Compress input bytes using 12-bit codes.
    /// Two codes pack into 3 bytes: [low8(code1)] [high4(code1)|low4(code2)] [high8(code2)]
    /// </summary>
    public static unsafe byte[] Compress(SymbolMap table, ReadOnlySpan<byte> input)
    {
        if (input.Length == 0)
            return [];

        // Collect codes first
        var codes = ArrayPool<ushort>.Shared.Rent(input.Length); // worst case: 1 code per byte
        int codeCount = 0;

        fixed (byte* inPtr = input)
        {
            byte* cur = inPtr;
            byte* end = inPtr + input.Length;

            while (cur < end)
            {
                int avail = (int)(end - cur);
                var sym = Symbol.FromPointer(cur, Math.Min(avail, 8));
                int code = table.FindLongestSymbol(sym);
                int len = table.Symbols[code].Length();

                codes[codeCount++] = (ushort)code;
                cur += len;
            }
        }

        // Pack 12-bit codes into bytes: 2 codes = 3 bytes
        int outLen = (codeCount / 2) * 3 + (codeCount % 2) * 2;
        byte[] output = new byte[outLen];
        int outPos = 0;

        for (int i = 0; i + 1 < codeCount; i += 2)
        {
            int c1 = codes[i];
            int c2 = codes[i + 1];
            output[outPos++] = (byte)(c1 & 0xFF);
            output[outPos++] = (byte)(((c1 >> 8) & 0x0F) | ((c2 & 0x0F) << 4));
            output[outPos++] = (byte)(c2 >> 4);
        }

        // Tail: 1 remaining code → 2 bytes
        if (codeCount % 2 == 1)
        {
            int c = codes[codeCount - 1];
            output[outPos++] = (byte)(c & 0xFF);
            output[outPos++] = (byte)((c >> 8) & 0x0F);
        }

        ArrayPool<ushort>.Shared.Return(codes);
        return output;
    }

    /// <summary>Compress multiple strings.</summary>
    public static (byte[] compressedData, int[] lengths, int[] codeCounts) CompressBatch(
        SymbolMap table, ReadOnlySpan<byte[]> strings)
    {
        var lengths = new int[strings.Length];
        var codeCounts = new int[strings.Length];
        var allCompressed = new List<byte>();

        for (int i = 0; i < strings.Length; i++)
        {
            var compressed = Compress(table, strings[i]);
            lengths[i] = compressed.Length;
            allCompressed.AddRange(compressed);

            // Calculate code count for decompression
            // Each pair of codes = 3 bytes, tail code = 2 bytes
            if (compressed.Length % 3 == 0)
                codeCounts[i] = (compressed.Length / 3) * 2;
            else
                codeCounts[i] = (compressed.Length / 3) * 2 + 1;
        }

        return ([.. allCompressed], lengths, codeCounts);
    }
}
