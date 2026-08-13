// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.IO;
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
    /// Decompress <paramref name="compressed"/> into <paramref name="destination"/>, reporting
    /// which of the two failure modes occurred.
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> on success;
    /// <see cref="OperationStatus.DestinationTooSmall"/> if <paramref name="destination"/> cannot
    /// hold the output (size it with <see cref="MaxDecompressedLength"/> to rule this out);
    /// <see cref="OperationStatus.InvalidData"/> if <paramref name="compressed"/> is not a
    /// well-formed FSST12 code stream. <paramref name="written"/> is 0 unless the status is
    /// <see cref="OperationStatus.Done"/>, so partial output is never surfaced.
    /// </returns>
    /// <remarks>
    /// FSST12 has no Parquet symbol table type and so no spec obligation, but it is held to the
    /// same standard as the other two: a stream whose length cannot be a whole number of codes, or
    /// that names a code with no symbol behind it, is rejected rather than silently truncated.
    /// </remarks>
    public unsafe OperationStatus Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination, out int written)
    {
        written = 0;
        if (compressed.Length == 0) return OperationStatus.Done;

        // 3 bytes hold two codes and 2 bytes hold one, so a length of 3k+1 leaves a byte that
        // cannot begin a code.
        if (compressed.Length % 3 == 1) return OperationStatus.InvalidData;

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
                if (len1 == 0) return OperationStatus.InvalidData;
                ulong val1 = DecoderSymbols[code1];
                if (outPos + len1 > dstLen) return OperationStatus.DestinationTooSmall;
                if (outPos + 8 <= dstLen)
                    Unsafe.WriteUnaligned(outPtr + outPos, val1);
                else
                    WriteCareful(outPtr, outPos, val1, len1);
                outPos += len1;

                int len2 = Len[code2];
                if (len2 == 0) return OperationStatus.InvalidData;
                ulong val2 = DecoderSymbols[code2];
                if (outPos + len2 > dstLen) return OperationStatus.DestinationTooSmall;
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
                if (len == 0) return OperationStatus.InvalidData;
                ulong val = DecoderSymbols[code];
                if (outPos + len > dstLen) return OperationStatus.DestinationTooSmall;
                if (outPos + 8 <= dstLen)
                    Unsafe.WriteUnaligned(outPtr + outPos, val);
                else
                    WriteCareful(outPtr, outPos, val, len);
                outPos += len;
            }
        }

        written = outPos;
        return OperationStatus.Done;
    }

    /// <summary>
    /// Decompress <paramref name="compressed"/> into <paramref name="destination"/>.
    /// Returns false (and sets <paramref name="written"/> to 0) if <paramref name="destination"/>
    /// is too small or <paramref name="compressed"/> is malformed; use
    /// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int)"/> to tell the two apart, or
    /// size the destination with <see cref="MaxDecompressedLength"/> so only the latter is possible.
    /// </summary>
    public bool TryDecompress(ReadOnlySpan<byte> compressed, Span<byte> destination, out int written)
        => Decompress(compressed, destination, out written) == OperationStatus.Done;

    private static void ThrowIfNotDone(OperationStatus status, string tooSmallMessage)
    {
        if (status == OperationStatus.Done) return;
        if (status == OperationStatus.InvalidData)
            throw new InvalidDataException("The compressed data is not a well-formed FSST12 code stream.");
        throw new InvalidOperationException(tooSmallMessage);
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
        ThrowIfNotDone(Decompress(compressed, dst, out int written), "Buffer writer returned a span smaller than the size hint.");
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
            ThrowIfNotDone(Decompress(compressed, rented.AsSpan(0, max), out int written), "MaxDecompressedLength was too small.");
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
            ThrowIfNotDone(Decompress(compressed, rented.AsSpan(0, max), out int written), "MaxDecompressedLength was too small.");
            return Encoding.UTF8.GetString(rented, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Decompress a batch of compressed strings into caller-supplied buffers, writing
    /// Arrow-style prefix-sum offsets so individual items can be addressed with
    /// <c>destination[destinationOffsets[i]..destinationOffsets[i+1]]</c>.
    /// </summary>
    /// <param name="compressedData">Concatenated compressed bytes for every string.</param>
    /// <param name="compressedLengths">Per-string compressed length, summing to <paramref name="compressedData"/>'s length.</param>
    /// <param name="destination">Destination buffer for the decompressed bytes. Size with <see cref="MaxDecompressedLength"/> when the uncompressed total is unknown.</param>
    /// <param name="destinationOffsets">Receives <c>compressedLengths.Length + 1</c> prefix-sum offsets; <c>destinationOffsets[0]</c> is always 0 and <c>destinationOffsets[^1]</c> equals <paramref name="totalWritten"/>.</param>
    /// <param name="totalWritten">Total bytes written to <paramref name="destination"/>.</param>
    /// <returns><c>false</c> if either output buffer is too small (and <paramref name="totalWritten"/> is set to 0); otherwise <c>true</c>.</returns>
    public bool TryDecompressBatch(
        ReadOnlySpan<byte> compressedData,
        ReadOnlySpan<int> compressedLengths,
        Span<byte> destination,
        Span<int> destinationOffsets,
        out int totalWritten)
        => DecompressBatch(compressedData, compressedLengths, destination, destinationOffsets, out totalWritten)
           == OperationStatus.Done;

    /// <summary>
    /// Batch counterpart of <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int)"/>, so a
    /// caller can tell a too-small buffer from corrupt input.
    /// </summary>
    public OperationStatus DecompressBatch(
        ReadOnlySpan<byte> compressedData,
        ReadOnlySpan<int> compressedLengths,
        Span<byte> destination,
        Span<int> destinationOffsets,
        out int totalWritten)
    {
        totalWritten = 0;
        if (destinationOffsets.Length != compressedLengths.Length + 1)
            return OperationStatus.InvalidData;

        int inOffset = 0;
        int outOffset = 0;
        destinationOffsets[0] = 0;

        for (int i = 0; i < compressedLengths.Length; i++)
        {
            int len = compressedLengths[i];
            if (len < 0 || inOffset + len > compressedData.Length)
                return OperationStatus.InvalidData;

            var status = Decompress(compressedData.Slice(inOffset, len), destination[outOffset..], out int written);
            if (status != OperationStatus.Done)
                return status;

            inOffset += len;
            outOffset += written;
            destinationOffsets[i + 1] = outOffset;
        }

        totalWritten = outOffset;
        return OperationStatus.Done;
    }
}
