// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Clast.Fsst;

/// <summary>
/// FSST16 decoder: decompresses data compressed with 16-bit codes.
/// Codes are little-endian <c>uint16</c>; code 65,535 is the escape marker and is followed by the
/// literal byte as a little-endian <c>uint16</c> in <c>[0, 255]</c>.
/// Instances are immutable after construction and safe to share across threads.
/// </summary>
public sealed class Fsst16Decoder
{
    private const int EscCode = Symbol16.EscCode;

    /// <summary>Symbol lengths indexed by code. Sized to the table, not to the full code space.</summary>
    private readonly byte[] _len;

    /// <summary>Bytes 0-7 of each symbol, indexed by code.</summary>
    private readonly ulong[] _lo;

    /// <summary>Bytes 8-15 of each symbol, indexed by code.</summary>
    private readonly ulong[] _hi;

    private Fsst16Decoder(int slots)
    {
        _len = new byte[slots];
        _lo = new ulong[slots];
        _hi = new ulong[slots];
    }

    /// <summary>Create a decoder from a symbol table.</summary>
    public static Fsst16Decoder FromSymbolTable(SymbolTable16 table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        var decoder = new Fsst16Decoder(table.NSymbols);
        for (int i = 0; i < table.NSymbols; i++)
        {
            var sym = table.Symbols[i];
            decoder._len[i] = (byte)sym.Length();
            decoder._lo[i] = sym.Lo;
            decoder._hi[i] = sym.Hi;
        }
        return decoder;
    }

    /// <summary>
    /// Create a decoder from pre-extracted FSST16 symbols, indexed by code. Slot <paramref name="i"/>
    /// defines the symbol for code <paramref name="i"/>: its length is <paramref name="lengths"/>[i]
    /// and its bytes occupy <paramref name="packedValues"/>[i*16 .. i*16+16] in little-endian order.
    /// Slots with length 0 are unused.
    ///
    /// This API is framing-agnostic — callers are expected to have already parsed any wire format
    /// (for example a Parquet FSST symbol-table page) into per-code lengths and 16-byte slots.
    /// Code 65,535 is reserved as the escape code; if 65,536 slots are supplied, the last must have
    /// length 0.
    /// </summary>
    /// <param name="lengths">Per-code symbol lengths (0..16). Length 0 marks an unused slot. At most 65,536 entries.</param>
    /// <param name="packedValues">Per-code 16-byte little-endian symbol values; must be exactly <c>16 * lengths.Length</c> bytes.</param>
    public static Fsst16Decoder FromSymbols(ReadOnlySpan<byte> lengths, ReadOnlySpan<byte> packedValues)
    {
        if (lengths.Length > Symbol16.CodeMax)
            throw new ArgumentException($"FSST16 supports at most {Symbol16.CodeMax} symbol slots.", nameof(lengths));
        if (packedValues.Length != lengths.Length * 16)
            throw new ArgumentException("packedValues must contain exactly 16 bytes per length entry.", nameof(packedValues));
        if (lengths.Length == Symbol16.CodeMax && lengths[EscCode] != 0)
            throw new ArgumentException($"Code {EscCode} is reserved as the escape code and must have length 0.", nameof(lengths));

        int n = Math.Min(lengths.Length, EscCode);
        var decoder = new Fsst16Decoder(n);
        for (int i = 0; i < n; i++)
        {
            byte len = lengths[i];
            if (len == 0) continue;
            if (len > Symbol16.MaxLength)
                throw new ArgumentException($"Symbol length {len} at code {i} exceeds the FSST16 maximum of {Symbol16.MaxLength}.", nameof(lengths));

            decoder._len[i] = len;
            decoder._lo[i] = BinaryPrimitives.ReadUInt64LittleEndian(packedValues.Slice(i * 16, 8)) & Symbol16.LoMask[len];
            decoder._hi[i] = BinaryPrimitives.ReadUInt64LittleEndian(packedValues.Slice(i * 16 + 8, 8)) & Symbol16.HiMask[len];
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
        // 2 compressed bytes hold one code; each code emits up to 16 output bytes. An escape costs
        // 4 compressed bytes for 1 output byte, so it never beats this bound.
        long max = (((long)compressedLength + 1) / 2) * Symbol16.MaxLength;
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
    /// well-formed FSST16 code stream. <paramref name="written"/> is 0 unless the status is
    /// <see cref="OperationStatus.Done"/>. Decoding writes as it goes, so on either failure
    /// <paramref name="destination"/> may already hold bytes decoded before the problem was
    /// detected; treat its contents as undefined unless the status is
    /// <see cref="OperationStatus.Done"/>.
    /// </returns>
    /// <remarks>
    /// Rejects exactly what the Parquet FSST spec's §8.3 decode algorithm calls an error: an
    /// odd-length stream, an escape marker not followed by a full <c>uint16</c> literal, a literal
    /// above 255, and a code at or beyond the symbol count. Validation is per call, so pass one
    /// value at a time — an escape must not be able to borrow the next value's bytes (§5.2), which
    /// is what <see cref="DecompressBatch"/> and <see cref="TryDecompressBatch"/> arrange by
    /// slicing first.
    /// </remarks>
    public unsafe OperationStatus Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination, out int written)
    {
        written = 0;
        if (compressed.Length == 0) return OperationStatus.Done;

        // §8.3: `if len(compressed_bytes) % 2 != 0: error truncated uint16`.
        if ((compressed.Length & 1) != 0) return OperationStatus.InvalidData;

        int outPos = 0;
        int dstLen = destination.Length;
        int slots = _len.Length;

        fixed (byte* inPtr = compressed)
        fixed (byte* outPtr = destination)
        {
            byte* cur = inPtr;
            byte* end = inPtr + compressed.Length;

            // The length is even, so a whole code is always available at the top of the loop.
            while (cur < end)
            {
                int code = cur[0] | (cur[1] << 8);
                cur += 2;

                if (code == EscCode)
                {
                    // The literal is a little-endian uint16 in [0, 255], not a bare byte: §8.3 reads
                    // it with read_uint16_le and errors when it exceeds 255.
                    if (cur + 2 > end) return OperationStatus.InvalidData;  // truncated escape (§5.2)
                    if (cur[1] != 0) return OperationStatus.InvalidData;    // literal > 255
                    if (outPos >= dstLen) return OperationStatus.DestinationTooSmall;
                    outPtr[outPos++] = cur[0];
                    cur += 2;
                    continue;
                }

                // §8.3: `if code >= symbol_table.symbol_count: error invalid symbol code`. A slot
                // inside the table but left empty is the same thing — a conformant symbol table has
                // no zero-length symbols, since length_histogram only covers lengths 1 and up.
                if (code >= slots) return OperationStatus.InvalidData;

                int len = _len[code];
                if (len == 0) return OperationStatus.InvalidData;

                if (outPos + len > dstLen) return OperationStatus.DestinationTooSmall;

                if (outPos + Symbol16.MaxLength <= dstLen)
                {
                    // Fast path: write all 16 bytes; everything above len is zero by construction
                    // and is overwritten by the next symbol.
                    Unsafe.WriteUnaligned(outPtr + outPos, _lo[code]);
                    Unsafe.WriteUnaligned(outPtr + outPos + 8, _hi[code]);
                }
                else
                {
                    WriteCareful(outPtr, outPos, _lo[code], _hi[code], len);
                }
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

    /// <summary>
    /// Translate a non-<see cref="OperationStatus.Done"/> status into an exception for the
    /// convenience overloads, which size their own destination and so can only fail on bad input.
    /// </summary>
    private static void ThrowIfNotDone(OperationStatus status, string tooSmallMessage)
    {
        if (status == OperationStatus.Done) return;
        if (status == OperationStatus.InvalidData)
            throw new InvalidDataException("The compressed data is not a well-formed FSST16 code stream.");
        throw new InvalidOperationException(tooSmallMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void WriteCareful(byte* outPtr, int outPos, ulong lo, ulong hi, int len)
    {
        int n = Math.Min(len, 8);
        for (int i = 0; i < n; i++)
            outPtr[outPos + i] = (byte)(lo >> (i * 8));
        for (int i = 8; i < len; i++)
            outPtr[outPos + i] = (byte)(hi >> ((i - 8) * 8));
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

    /// <summary>Decompress a single compressed byte span.</summary>
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
    /// <returns><c>false</c> if either output buffer is too small, or any value is malformed (and
    /// <paramref name="totalWritten"/> is set to 0); otherwise <c>true</c>. Use
    /// <see cref="DecompressBatch"/> to tell the two apart. <paramref name="destination"/> and
    /// <paramref name="destinationOffsets"/> may be partly overwritten when this returns
    /// <c>false</c>.</returns>
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
    /// caller can tell a too-small buffer from corrupt input. Each value is decoded from its own
    /// slice, so an escape at the end of one value cannot consume the next value's bytes (§5.2).
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.InvalidData"/> if the offsets span is missized, a length is
    /// negative or overruns <paramref name="compressedData"/>, or any value is malformed;
    /// otherwise the status of the first value that did not decode, or
    /// <see cref="OperationStatus.Done"/>.
    /// </returns>
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
