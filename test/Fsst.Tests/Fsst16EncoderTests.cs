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

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x41, 0x00, 0xFF, 0xFF, 0x42, 0x00 }, result);
        Assert.Equal(new byte[] { 0x41, 0x42 }, Fsst16Decoder.FromSymbolTable(table).Decompress(result));
    }

    [Fact]
    public void MaxCompressedLength_IsFourTimesInput()
    {
        // 2-byte escape marker plus a 2-byte literal, for every input byte.
        Assert.Equal(0, Fsst16Encoder.MaxCompressedLength(0));
        Assert.Equal(400, Fsst16Encoder.MaxCompressedLength(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Encoder.MaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Encoder.MaxCompressedLength(int.MaxValue));
    }

    [Fact]
    public void Compress_EscapedOutputIsAlwaysAnEvenNumberOfBytes()
    {
        // §8.3 opens with `if len(compressed_bytes) % 2 != 0: error truncated uint16`, so a
        // conformant reader rejects an odd-length stream outright. A one-byte escape literal would
        // produce one for any odd number of escapes.
        var table = new SymbolTable16(16);

        for (int length = 1; length <= 9; length++)
        {
            var input = new byte[length];
            for (int i = 0; i < length; i++) input[i] = (byte)(0x40 + i);

            var compressed = Fsst16Encoder.Compress(table, input);

            Assert.True(compressed.Length % 2 == 0,
                $"{length}-byte input produced {compressed.Length} compressed bytes, which is odd");
            Assert.Equal(input, Fsst16Decoder.FromSymbolTable(table).Decompress(compressed));
        }
    }

    [Fact]
    public void Compress_EscapeLiteralHighByteIsZero()
    {
        // §8.3 reads the literal with read_uint16_le and errors when it exceeds 255, so the high
        // byte must be zero even for input bytes above 0x7F.
        var table = new SymbolTable16(16);
        var compressed = Fsst16Encoder.Compress(table, [0xFF, 0x80]);

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0x80, 0x00 }, compressed);
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
