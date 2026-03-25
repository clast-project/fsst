using System.Runtime.CompilerServices;

namespace Fsst;

/// <summary>
/// FSST12 decoder: decompresses data compressed with 12-bit codes.
/// Two codes are packed into 3 bytes.
/// </summary>
public sealed class Fsst12Decoder
{
    /// <summary>Symbol lengths indexed by code (0-4095).</summary>
    public readonly byte[] Len = new byte[SymbolMap.CodeMax12];

    /// <summary>Symbol values indexed by code (0-4095).</summary>
    public readonly ulong[] DecoderSymbols = new ulong[SymbolMap.CodeMax12];

    /// <summary>Create a decoder from a symbol map.</summary>
    public static Fsst12Decoder FromSymbolMap(SymbolMap map)
    {
        var decoder = new Fsst12Decoder();

        // Initialize single-byte symbols
        for (int i = 0; i < 256; i++)
        {
            decoder.Len[i] = 1;
            decoder.DecoderSymbols[i] = (ulong)i;
        }

        // Initialize real symbols
        for (int i = SymbolMap.CodeBase12; i < SymbolMap.CodeBase12 + map.NSymbols; i++)
        {
            var sym = map.Symbols[i];
            decoder.Len[i] = (byte)sym.Length();
            decoder.DecoderSymbols[i] = sym.Val;
        }

        return decoder;
    }

    /// <summary>
    /// Decompress 12-bit packed codes.
    /// Format: 2 codes in 3 bytes, tail 1 code in 2 bytes.
    /// </summary>
    public unsafe byte[] Decompress(ReadOnlySpan<byte> compressed, int originalLength = -1)
    {
        if (compressed.Length == 0)
            return [];

        int allocLen = originalLength > 0 ? originalLength : compressed.Length * 8;
        byte[] output = new byte[allocLen];
        int outPos = 0;

        fixed (byte* inPtr = compressed)
        fixed (byte* outPtr = output)
        {
            byte* cur = inPtr;
            byte* end = inPtr + compressed.Length;

            // Process pairs of codes (3 bytes each)
            while (cur + 2 < end)
            {
                int b0 = cur[0];
                int b1 = cur[1];
                int b2 = cur[2];
                cur += 3;

                int code1 = b0 | ((b1 & 0x0F) << 8);
                int code2 = (b1 >> 4) | (b2 << 4);

                // Decode first code
                int len1 = Len[code1];
                ulong val1 = DecoderSymbols[code1];
                if (outPos + 8 <= output.Length)
                    Unsafe.WriteUnaligned(outPtr + outPos, val1);
                else
                    WriteCareful(outPtr, output.Length, outPos, val1, len1);
                outPos += len1;

                // Decode second code
                int len2 = Len[code2];
                ulong val2 = DecoderSymbols[code2];
                if (outPos + 8 <= output.Length)
                    Unsafe.WriteUnaligned(outPtr + outPos, val2);
                else
                    WriteCareful(outPtr, output.Length, outPos, val2, len2);
                outPos += len2;
            }

            // Tail: 2 remaining bytes = 1 code
            if (cur + 1 < end)
            {
                int b0 = cur[0];
                int b1 = cur[1];
                int code = b0 | ((b1 & 0x0F) << 8);

                int len = Len[code];
                ulong val = DecoderSymbols[code];
                if (outPos + 8 <= output.Length)
                    Unsafe.WriteUnaligned(outPtr + outPos, val);
                else
                    WriteCareful(outPtr, output.Length, outPos, val, len);
                outPos += len;
            }
        }

        if (outPos == output.Length)
            return output;

        return output.AsSpan(0, outPos).ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void WriteCareful(byte* outPtr, int outLen, int outPos, ulong val, int len)
    {
        for (int i = 0; i < len && outPos + i < outLen; i++)
            outPtr[outPos + i] = (byte)(val >> (i * 8));
    }

    /// <summary>Decompress multiple strings.</summary>
    public byte[][] DecompressBatch(ReadOnlySpan<byte> compressedData, ReadOnlySpan<int> lengths)
    {
        var result = new byte[lengths.Length][];
        int offset = 0;

        for (int i = 0; i < lengths.Length; i++)
        {
            var segment = compressedData.Slice(offset, lengths[i]);
            result[i] = Decompress(segment);
            offset += lengths[i];
        }

        return result;
    }
}
