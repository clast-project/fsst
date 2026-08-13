// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;

namespace Clast.Fsst.Tests;

public class Fsst16EncoderTests
{
    [Fact]
    public void BuildSymbolTable_EmptyInput_StillCoversEveryByte()
    {
        var table = Fsst16Encoder.BuildSymbolTable([]);

        // 65,535 codes leave no reason to escape, so even the degenerate table holds all 256 bytes.
        Assert.Equal(256, table.SymbolCount);
        Assert.Equal(16, table.MaxSymbolLength);
    }

    [Fact]
    public void BuildSymbolTable_RepeatedData_ProducesMultiByteSymbols()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 400)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        Assert.True(table.SymbolCount > 256, $"Expected multi-byte symbols beyond the 256 base codes, got {table.SymbolCount}.");
    }

    [Fact]
    public void BuildSymbolTable_AssignsCodesInAscendingLengthOrder()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 300)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        var lengths = new byte[table.SymbolCount];
        var values = new byte[table.SymbolCount * 16];
        table.ExportRaw(lengths, values);

        for (int i = 1; i < lengths.Length; i++)
            Assert.True(lengths[i] >= lengths[i - 1], $"Length at code {i} ({lengths[i]}) is shorter than at code {i - 1} ({lengths[i - 1]}).");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    public void BuildSymbolTable_RejectsOutOfRangeMaxSymbolLength(int maxSymbolLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Fsst16Encoder.BuildSymbolTable([Encoding.UTF8.GetBytes("abc")], maxSymbolLength));
    }

    [Fact]
    public void Compress_EmptyInput_ReturnsEmpty()
    {
        var table = Fsst16Encoder.BuildSymbolTable([]);
        Assert.Empty(Fsst16Encoder.Compress(table, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compress_SingleByte_ProducesOneLittleEndianCode()
    {
        var table = Fsst16Encoder.BuildSymbolTable([]);
        var result = Fsst16Encoder.Compress(table, [(byte)'A']);

        Assert.Equal(2, result.Length);
        Assert.Equal((byte)'A', result[0]); // single bytes take codes 0..255 in byte order
        Assert.Equal(0, result[1]);
    }

    [Fact]
    public void Compress_UncoveredByte_EmitsEscapePlusLiteral()
    {
        // A table with no symbols at all: every byte has to escape.
        var table = new SymbolTable16(16);
        var result = Fsst16Encoder.Compress(table, [0x41, 0x42]);

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x41, 0xFF, 0xFF, 0x42 }, result);
        Assert.Equal(new byte[] { 0x41, 0x42 }, Fsst16Decoder.FromSymbolTable(table).Decompress(result));
    }

    [Fact]
    public void MaxCompressedLength_IsThreeTimesInput()
    {
        Assert.Equal(0, Fsst16Encoder.MaxCompressedLength(0));
        Assert.Equal(300, Fsst16Encoder.MaxCompressedLength(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Encoder.MaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Encoder.MaxCompressedLength(int.MaxValue));
    }

    [Fact]
    public void MaxCompressedLength_BoundsTheAllEscapeWorstCase()
    {
        var table = new SymbolTable16(16);
        var input = new byte[64];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)i;

        var compressed = Fsst16Encoder.Compress(table, input);
        Assert.Equal(Fsst16Encoder.MaxCompressedLength(input.Length), compressed.Length);
    }

    [Fact]
    public void TryCompress_DestinationTooSmall_ReturnsFalse()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        Assert.False(Fsst16Encoder.TryCompress(table, data, new byte[1], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryCompress_ExactSizedDestination_Succeeds()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        var scratch = new byte[Fsst16Encoder.MaxCompressedLength(data.Length)];
        Assert.True(Fsst16Encoder.TryCompress(table, data, scratch, out int needed));

        Assert.True(Fsst16Encoder.TryCompress(table, data, new byte[needed], out int written));
        Assert.Equal(needed, written);
        Assert.False(Fsst16Encoder.TryCompress(table, data, new byte[needed - 1], out _));
    }

    [Fact]
    public void Compress_ToBufferWriter_MatchesArrayOverload()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 50)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        var writer = new ListBufferWriter();
        Fsst16Encoder.Compress(table, data, writer);

        Assert.Equal(Fsst16Encoder.Compress(table, data), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Compress_String_MatchesUtf8Bytes()
    {
        const string text = "the quick brown fox";
        var data = Encoding.UTF8.GetBytes(text);
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        Assert.Equal(Fsst16Encoder.Compress(table, data), Fsst16Encoder.Compress(table, text));
        Assert.Empty(Fsst16Encoder.Compress(table, string.Empty));
    }

    [Fact]
    public void CompressBatch_EmptyRows_ReturnsEmptyData()
    {
        var table = Fsst16Encoder.BuildSymbolTable([]);
        var (data, lengths) = Fsst16Encoder.CompressBatch(table, [[], []]);

        Assert.Empty(data);
        Assert.Equal([0, 0], lengths);
    }
}
