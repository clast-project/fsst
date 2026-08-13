// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.IO;
using System.Text;

namespace Clast.Fsst.Tests;

/// <summary>
/// The Parquet FSST spec requires a reader to fail on corruption rather than produce output from it
/// (§8.1, §8.3, §5.2). These pin each rejection the §8.3 decode algorithm calls for, and the
/// distinction between corrupt input and a destination that is merely too small.
/// </summary>
public class MalformedInputTests
{
    private static Fsst16Decoder Decoder16(params byte[][] symbols)
    {
        var lengths = new byte[symbols.Length];
        var values = new byte[symbols.Length * 16];
        for (int i = 0; i < symbols.Length; i++)
        {
            lengths[i] = (byte)symbols[i].Length;
            symbols[i].CopyTo(values, i * 16);
        }
        return Fsst16Decoder.FromSymbols(lengths, values);
    }

    private static FsstDecoder Decoder8(params byte[][] symbols)
    {
        var lengths = new byte[symbols.Length];
        var values = new byte[symbols.Length * 8];
        for (int i = 0; i < symbols.Length; i++)
        {
            lengths[i] = (byte)symbols[i].Length;
            symbols[i].CopyTo(values, i * 8);
        }
        return FsstDecoder.FromSymbols(lengths, values);
    }

    private static OperationStatus Decode16(Fsst16Decoder decoder, byte[] compressed)
        => decoder.Decompress(compressed, new byte[256], out _);

    private static OperationStatus Decode8(FsstDecoder decoder, byte[] compressed)
        => decoder.Decompress(compressed, new byte[256], out _);

    // ---------- FSST16, per §8.3 ----------

    [Fact]
    public void Fsst16_OddLengthStream_IsInvalid()
    {
        // §8.3: `if len(compressed_bytes) % 2 != 0: error truncated uint16`
        var decoder = Decoder16("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x00, 0x00, 0x07]));
    }

    [Fact]
    public void Fsst16_CodeAtOrBeyondSymbolCount_IsInvalid()
    {
        // §8.3: `if code >= symbol_table.symbol_count: error invalid symbol code`
        var decoder = Decoder16("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x01, 0x00]));
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x34, 0x12]));
    }

    [Fact]
    public void Fsst16_UnusedSlotInsideTheTable_IsInvalid()
    {
        // A zero-length slot cannot come from a conformant symbol table, so it is corruption in the
        // same way an out-of-range code is. The two must agree.
        var decoder = Decoder16("ab"u8.ToArray(), []);
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x01, 0x00]));
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x02, 0x00]));
    }

    [Fact]
    public void Fsst16_TruncatedEscape_IsInvalid()
    {
        // §8.3: `if i == len(compressed_bytes): error truncated escape`
        var decoder = Decoder16("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0xFF, 0xFF]));
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0x00, 0x00, 0xFF, 0xFF]));
    }

    [Fact]
    public void Fsst16_LiteralAbove255_IsInvalid()
    {
        // §8.3: `if literal > 255: error invalid literal`
        var decoder = Decoder16("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode16(decoder, [0xFF, 0xFF, 0x41, 0x01]));
        Assert.Equal(OperationStatus.Done, Decode16(decoder, [0xFF, 0xFF, 0x41, 0x00]));
    }

    [Fact]
    public void Fsst16_WellFormedStreamStillDecodes()
    {
        var decoder = Decoder16("ab"u8.ToArray(), "cd"u8.ToArray());
        var dst = new byte[16];

        Assert.Equal(OperationStatus.Done, decoder.Decompress([0x00, 0x00, 0x01, 0x00, 0xFF, 0xFF, 0x5A, 0x00], dst, out int written));
        Assert.Equal(5, written);
        Assert.Equal("abcdZ"u8.ToArray(), dst.AsSpan(0, written).ToArray());
    }

    // ---------- FSST8, per §8.3's FSST branch ----------

    [Fact]
    public void Fsst8_TruncatedEscape_IsInvalid()
    {
        var decoder = Decoder8("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode8(decoder, [0xFF]));
        Assert.Equal(OperationStatus.InvalidData, Decode8(decoder, [0x00, 0xFF]));
    }

    [Fact]
    public void Fsst8_CodeAtOrBeyondSymbolCount_IsInvalid()
    {
        var decoder = Decoder8("ab"u8.ToArray());
        Assert.Equal(OperationStatus.InvalidData, Decode8(decoder, [0x01]));
        Assert.Equal(OperationStatus.InvalidData, Decode8(decoder, [0x7F]));
    }

    [Fact]
    public void Fsst8_WellFormedStreamStillDecodes()
    {
        var decoder = Decoder8("ab"u8.ToArray(), "cd"u8.ToArray());
        var dst = new byte[16];

        Assert.Equal(OperationStatus.Done, decoder.Decompress([0x00, 0x01, 0xFF, (byte)'!'], dst, out int written));
        Assert.Equal(5, written);
        Assert.Equal("abcd!"u8.ToArray(), dst.AsSpan(0, written).ToArray());
    }

    // ---------- FSST12 ----------

    [Fact]
    public void Fsst12_LengthThatCannotHoldWholeCodes_IsInvalid()
    {
        var decoder = Fsst12Decoder.FromSymbolMap(new SymbolMap());

        // 3k+1 bytes leaves a byte that cannot begin a code.
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(new byte[1], new byte[64], out _));
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(new byte[4], new byte[64], out _));

        Assert.Equal(OperationStatus.Done, decoder.Decompress(new byte[2], new byte[64], out _));
        Assert.Equal(OperationStatus.Done, decoder.Decompress(new byte[3], new byte[64], out _));
    }

    [Fact]
    public void Fsst12_CodeWithNoSymbol_IsInvalid()
    {
        var map = new SymbolMap();
        map.Add(Symbol.FromSpan("xy"u8));
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        // Code 256 is the one real symbol; 257 has nothing behind it.
        Assert.Equal(OperationStatus.Done, decoder.Decompress([0x00, 0x01], new byte[64], out _));
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress([0x01, 0x01], new byte[64], out _));
    }

    // ---------- the two failure modes stay distinguishable ----------

    [Fact]
    public void DestinationTooSmallIsNotConfusedWithCorruption()
    {
        var decoder = Decoder16("abcdefgh"u8.ToArray());
        byte[] compressed = [0x00, 0x00];

        Assert.Equal(OperationStatus.DestinationTooSmall, decoder.Decompress(compressed, new byte[4], out int written));
        Assert.Equal(0, written);

        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, new byte[8], out written));
        Assert.Equal(8, written);
    }

    [Fact]
    public void TryDecompress_ReportsFalseForBothFailureModes()
    {
        var decoder = Decoder16("ab"u8.ToArray());

        Assert.False(decoder.TryDecompress([0x00, 0x00], new byte[1], out int written)); // too small
        Assert.Equal(0, written);

        Assert.False(decoder.TryDecompress([0x01, 0x00], new byte[16], out written));    // corrupt
        Assert.Equal(0, written);

        Assert.True(decoder.TryDecompress([0x00, 0x00], new byte[16], out written));
        Assert.Equal(2, written);
    }

    [Fact]
    public void ConvenienceOverloadsThrowInvalidDataOnCorruption()
    {
        var decoder16 = Decoder16("ab"u8.ToArray());
        var decoder8 = Decoder8("ab"u8.ToArray());
        byte[] bad16 = [0x01, 0x00];
        byte[] bad8 = [0x01];

        Assert.Throws<InvalidDataException>(() => decoder16.Decompress(bad16));
        Assert.Throws<InvalidDataException>(() => decoder16.DecompressString(bad16));
        Assert.Throws<InvalidDataException>(() => decoder16.Decompress(bad16, new ListBufferWriter()));

        Assert.Throws<InvalidDataException>(() => decoder8.Decompress(bad8));
        Assert.Throws<InvalidDataException>(() => decoder8.DecompressString(bad8));
    }

    [Fact]
    public void BatchRejectsACorruptValueWithoutDecodingPastIt()
    {
        var decoder = Decoder16("ab"u8.ToArray());

        byte[] data = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00]; // good, corrupt, good
        int[] lengths = [2, 2, 2];
        var dst = new byte[64];
        var offsets = new int[4];

        Assert.Equal(OperationStatus.InvalidData, decoder.DecompressBatch(data, lengths, dst, offsets, out int total));
        Assert.Equal(0, total);
        Assert.False(decoder.TryDecompressBatch(data, lengths, dst, offsets, out _));
    }

    [Fact]
    public void BatchDoesNotLetAnEscapeBorrowTheNextValuesBytes()
    {
        // §5.2 — the value boundary is what stops a trailing escape from consuming the neighbour.
        var decoder = Decoder16("ab"u8.ToArray());

        byte[] data = [0xFF, 0xFF, 0x00, 0x00]; // one value would be a bare escape marker
        int[] lengths = [2, 2];
        var offsets = new int[3];

        Assert.Equal(OperationStatus.InvalidData,
            decoder.DecompressBatch(data, lengths, new byte[64], offsets, out _));
    }

    [Fact]
    public void RoundTrippedDataIsNeverRejected()
    {
        // Guards against the checks being too eager on real output from all three encoders.
        var rows = new byte[200][];
        var rnd = new Random(11);
        for (int i = 0; i < rows.Length; i++)
            rows[i] = Encoding.UTF8.GetBytes($"https://example.com/{rnd.Next():x8}/item-{rnd.Next(50)}");

        var t8 = FsstEncoder.BuildSymbolTable(rows);
        var t12 = Fsst12Encoder.BuildSymbolTable(rows);
        var t16 = Fsst16Encoder.BuildSymbolTable(rows);

        var d8 = FsstDecoder.FromSymbolTable(t8);
        var d12 = Fsst12Decoder.FromSymbolMap(t12);
        var d16 = Fsst16Decoder.FromSymbolTable(t16);

        foreach (var row in rows)
        {
            Assert.Equal(row, d8.Decompress(FsstEncoder.Compress(t8, row)));
            Assert.Equal(row, d12.Decompress(Fsst12Encoder.Compress(t12, row)));
            Assert.Equal(row, d16.Decompress(Fsst16Encoder.Compress(t16, row)));
        }
    }
}
