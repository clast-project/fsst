// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST8 decoder: decompresses data compressed with FsstEncoder.
/// Instances are immutable after construction and safe to share across threads.
/// </summary>
public sealed class FsstDecoder
{
    private const int EscCode = 255;

    /// <summary>Symbol lengths indexed by code (0-254).</summary>
    private readonly byte[] Len = new byte[255];

    /// <summary>Symbol values indexed by code (0-254), stored as ulong (up to 8 bytes).</summary>
    private readonly ulong[] DecoderSymbols = new ulong[255];

    private FsstDecoder() { }

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

    /// <summary>
    /// Create a decoder from pre-extracted FSST8 symbols, indexed by code. Slot <paramref name="i"/>
    /// defines the symbol for code <paramref name="i"/>: its length is <paramref name="lengths"/>[i]
    /// and its bytes occupy <paramref name="packedValues"/>[i*8 .. i*8+8] in little-endian order.
    /// Slots with length 0 are unused.
    ///
    /// This API is framing-agnostic — callers are expected to have already parsed any wire format
    /// (cwida <c>fsst_export</c>, Lance's TSSF block, etc.) into per-code lengths and 8-byte slots.
    /// Code 255 is reserved as the escape code; if 256 slots are supplied, slot 255 must have length 0.
    /// </summary>
    /// <param name="lengths">Per-code symbol lengths (0..8). Length 0 marks an unused slot. At most 256 entries.</param>
    /// <param name="packedValues">Per-code 8-byte little-endian symbol values; must be exactly <c>8 * lengths.Length</c> bytes.</param>
    public static FsstDecoder FromSymbols(ReadOnlySpan<byte> lengths, ReadOnlySpan<byte> packedValues)
    {
        if (lengths.Length > 256)
            throw new ArgumentException("FSST8 supports at most 256 symbol slots.", nameof(lengths));
        if (packedValues.Length != lengths.Length * 8)
            throw new ArgumentException("packedValues must contain exactly 8 bytes per length entry.", nameof(packedValues));
        if (lengths.Length == 256 && lengths[255] != 0)
            throw new ArgumentException("Code 255 is reserved as the escape code and must have length 0.", nameof(lengths));

        var decoder = new FsstDecoder();
        int n = Math.Min(lengths.Length, 255);
        for (int i = 0; i < n; i++)
        {
            byte len = lengths[i];
            if (len == 0) continue;
            if (len > Symbol.MaxLength)
                throw new ArgumentException($"Symbol length {len} at code {i} exceeds the FSST8 maximum of {Symbol.MaxLength}.", nameof(lengths));

            decoder.Len[i] = len;
            decoder.DecoderSymbols[i] = BinaryPrimitives.ReadUInt64LittleEndian(packedValues.Slice(i * 8, 8));
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
        // Each compressed byte expands to at most 8 output bytes (a real symbol of max length).
        // Escape sequences take 2 compressed bytes for 1 output byte, so they don't beat this bound.
        long max = (long)compressedLength * 8;
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

            while (cur < end)
            {
                byte code = *cur++;

                if (code == EscCode)
                {
                    if (cur >= end) break; // dangling escape; ignore trailing byte
                    if (outPos >= dstLen) return false;
                    outPtr[outPos++] = *cur++;
                }
                else
                {
                    int len = Len[code];
                    ulong val = DecoderSymbols[code];

                    if (outPos + len > dstLen) return false;

                    if (outPos + 8 <= dstLen)
                    {
                        // Fast path: write 8 bytes (the high 8-len bytes of val are zero by construction).
                        Unsafe.WriteUnaligned(outPtr + outPos, val);
                    }
                    else
                    {
                        for (int i = 0; i < len; i++)
                            outPtr[outPos + i] = (byte)(val >> (i * 8));
                    }
                    outPos += len;
                }
            }
        }

        written = outPos;
        return true;
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

    /// <summary>Decompress a single compressed byte span.</summary>
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
    {
        totalWritten = 0;
        if (destinationOffsets.Length != compressedLengths.Length + 1)
            return false;

        int inOffset = 0;
        int outOffset = 0;
        destinationOffsets[0] = 0;

        for (int i = 0; i < compressedLengths.Length; i++)
        {
            int len = compressedLengths[i];
            if (len < 0 || inOffset + len > compressedData.Length)
                return false;

            if (!TryDecompress(compressedData.Slice(inOffset, len), destination[outOffset..], out int written))
                return false;

            inOffset += len;
            outOffset += written;
            destinationOffsets[i + 1] = outOffset;
        }

        totalWritten = outOffset;
        return true;
    }
}
