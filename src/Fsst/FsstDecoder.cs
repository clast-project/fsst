using System.Runtime.CompilerServices;

namespace Fsst;

/// <summary>
/// FSST8 decoder: decompresses data compressed with FsstEncoder.
/// </summary>
public sealed class FsstDecoder
{
    private const int EscCode = 255;

    /// <summary>Symbol lengths indexed by code (0-254).</summary>
    public readonly byte[] Len = new byte[255];

    /// <summary>Symbol values indexed by code (0-254), stored as ulong (up to 8 bytes).</summary>
    public readonly ulong[] DecoderSymbols = new ulong[255];

    /// <summary>Create a decoder from a finalized symbol table.</summary>
    public static FsstDecoder FromSymbolTable(SymbolTable table)
    {
        var decoder = new FsstDecoder();

        // After finalization, symbols[0..nSymbols-1] contain the real symbols
        // with their finalized codes matching their array index.
        // Code 255 is reserved as escape code.
        for (int i = 0; i < table.NSymbols && i < 255; i++)
        {
            var sym = table.Symbols[i];
            decoder.Len[i] = (byte)sym.Length();
            decoder.DecoderSymbols[i] = sym.Val;
        }

        return decoder;
    }

    /// <summary>Decompress a single compressed byte span.</summary>
    public unsafe byte[] Decompress(ReadOnlySpan<byte> compressed, int originalLength = -1)
    {
        if (compressed.Length == 0)
            return [];

        // If we don't know original length, allocate worst case (each code = 8 bytes)
        int allocLen = originalLength > 0 ? originalLength : compressed.Length * 8;
        byte[] output = new byte[allocLen];
        int outPos = 0;

        fixed (byte* inPtr = compressed)
        fixed (byte* outPtr = output)
        {
            byte* cur = inPtr;
            byte* end = inPtr + compressed.Length;

            while (cur < end)
            {
                byte code = *cur++;

                if (code == EscCode)
                {
                    // Escaped literal byte
                    if (cur >= end) break;
                    outPtr[outPos++] = *cur++;
                }
                else
                {
                    // Symbol lookup
                    int len = Len[code];
                    ulong val = DecoderSymbols[code];

                    // Write up to 8 bytes (may over-write, but we allocated enough)
                    if (outPos + 8 <= output.Length)
                    {
                        Unsafe.WriteUnaligned(outPtr + outPos, val);
                    }
                    else
                    {
                        // Careful write near end
                        for (int i = 0; i < len && outPos + i < output.Length; i++)
                            outPtr[outPos + i] = (byte)(val >> (i * 8));
                    }
                    outPos += len;
                }
            }
        }

        if (outPos == output.Length)
            return output;

        return output.AsSpan(0, outPos).ToArray();
    }

    /// <summary>Decompress multiple strings given their compressed lengths and offsets.</summary>
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
