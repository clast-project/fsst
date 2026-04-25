// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST12 decoder: decompresses data compressed with 12-bit codes.
/// Two codes are packed into 3 bytes.
/// Instances are immutable after construction and safe to share across threads.
/// </summary>
public sealed class Fsst12Decoder
{
    /// <summary>Symbol lengths indexed by code (0-4095).</summary>
    private readonly byte[] Len = new byte[SymbolMap.CodeMax12];

    /// <summary>Symbol values indexed by code (0-4095).</summary>
    private readonly ulong[] DecoderSymbols = new ulong[SymbolMap.CodeMax12];

    private Fsst12Decoder() { }

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
    /// Returns an upper bound on the number of bytes <see cref="Decompress(ReadOnlySpan{byte})"/>
    /// may produce for compressed input of the given length.
    /// </summary>
    public static int MaxDecompressedLength(int compressedLength)
    {
        if (compressedLength < 0) throw new ArgumentOutOfRangeException(nameof(compressedLength));
        // 3 compressed bytes hold 2 codes; each code emits up to 8 output bytes.
        long max = ((long)compressedLength + 2) / 3 * 16;
        if (max > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(compressedLength), "Input is too large.");
        return (int)max;
    }

    /// <summary>
    /// Decompress <paramref name="compressed"/> into <paramref name="destination"/>.
    /// Returns false (and sets <paramref name="written"/> to 0) if <paramref name="destination"/>
    /// is too small. Use <see cref="MaxDecompressedLength"/> for a safe upper bound.
    /// </summary>
    public unsafe bool TryDecompress(ReadOnlySpan<byte> compressed, Span<byte> destination, out int written)
    {
        written = 0;
        if (compressed.Length == 0) return true;

        int outPos = 0;
        int dstLen = destination.Length;

        fixed (byte* inPtr = compressed)
        fixed (byte* outPtr = destination)
        {
            byte* cur = inPtr;
            byte* end = inPtr + compressed.Length;

            // Process pairs of codes (3 bytes -> 2 codes).
            while (cur + 3 <= end)
            {
                int b0 = cur[0];
                int b1 = cur[1];
                int b2 = cur[2];
                cur += 3;

                int code1 = b0 | ((b1 & 0x0F) << 8);
                int code2 = (b1 >> 4) | (b2 << 4);

                int len1 = Len[code1];
                ulong val1 = DecoderSymbols[code1];
                if (outPos + len1 > dstLen) return false;
                if (outPos + 8 <= dstLen)
                    Unsafe.WriteUnaligned(outPtr + outPos, val1);
                else
                    WriteCareful(outPtr, outPos, val1, len1);
                outPos += len1;

                int len2 = Len[code2];
                ulong val2 = DecoderSymbols[code2];
                if (outPos + len2 > dstLen) return false;
                if (outPos + 8 <= dstLen)
                    Unsafe.WriteUnaligned(outPtr + outPos, val2);
                else
                    WriteCareful(outPtr, outPos, val2, len2);
                outPos += len2;
            }

            // Tail: 2 remaining bytes = 1 code.
            if (cur + 2 <= end)
            {
                int b0 = cur[0];
                int b1 = cur[1];
                int code = b0 | ((b1 & 0x0F) << 8);

                int len = Len[code];
                ulong val = DecoderSymbols[code];
                if (outPos + len > dstLen) return false;
                if (outPos + 8 <= dstLen)
                    Unsafe.WriteUnaligned(outPtr + outPos, val);
                else
                    WriteCareful(outPtr, outPos, val, len);
                outPos += len;
            }
        }

        written = outPos;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void WriteCareful(byte* outPtr, int outPos, ulong val, int len)
    {
        for (int i = 0; i < len; i++)
            outPtr[outPos + i] = (byte)(val >> (i * 8));
    }

    /// <summary>Decompress <paramref name="compressed"/> and append the result to <paramref name="writer"/>.</summary>
    public void Decompress(ReadOnlySpan<byte> compressed, IBufferWriter<byte> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (compressed.Length == 0) return;

        int max = MaxDecompressedLength(compressed.Length);
        Span<byte> dst = writer.GetSpan(max);
        if (!TryDecompress(compressed, dst, out int written))
            throw new InvalidOperationException("Buffer writer returned a span smaller than the size hint.");
        writer.Advance(written);
    }

    /// <summary>Decompress 12-bit packed codes.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length == 0) return [];

        int max = MaxDecompressedLength(compressed.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            if (!TryDecompress(compressed, rented.AsSpan(0, max), out int written))
                throw new InvalidOperationException("MaxDecompressedLength was too small.");
            var result = new byte[written];
            Array.Copy(rented, result, written);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Decompress to a UTF-8 string.</summary>
    public string DecompressString(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length == 0) return string.Empty;

        int max = MaxDecompressedLength(compressed.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            if (!TryDecompress(compressed, rented.AsSpan(0, max), out int written))
                throw new InvalidOperationException("MaxDecompressedLength was too small.");
            return Encoding.UTF8.GetString(rented, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
