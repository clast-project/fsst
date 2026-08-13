// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class Fsst16RoundTripTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void RoundTrip_Identity(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        var table = Fsst16Encoder.BuildSymbolTable([data]);
        var decoder = Fsst16Decoder.FromSymbolTable(table);

        var compressed = Fsst16Encoder.Compress(table, data);

        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    public void RoundTrip_HonoursMaxSymbolLength(int maxSymbolLength)
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz", 400)));
        var table = Fsst16Encoder.BuildSymbolTable([data], maxSymbolLength);

        Assert.Equal(maxSymbolLength, table.MaxSymbolLength);

        var lengths = new byte[table.SymbolCount];
        var values = new byte[table.SymbolCount * 16];
        table.ExportRaw(lengths, values);
        Assert.All(lengths, len => Assert.InRange(len, 1, maxSymbolLength));

        var decoder = Fsst16Decoder.FromSymbolTable(table);
        var compressed = Fsst16Encoder.Compress(table, data);
        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void RoundTrip_AllByteValues()
    {
        var alphabet = new byte[256];
        for (int i = 0; i < 256; i++) alphabet[i] = (byte)i;

        var data = new byte[256 * 40];
        for (int i = 0; i < 40; i++) alphabet.CopyTo(data.AsSpan(i * 256));

        var table = Fsst16Encoder.BuildSymbolTable([data]);
        var decoder = Fsst16Decoder.FromSymbolTable(table);

        var compressed = Fsst16Encoder.Compress(table, data);
        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void RoundTrip_BinaryDataNotInTrainingSet()
    {
        var trained = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 200)));
        var table = Fsst16Encoder.BuildSymbolTable([trained]);
        var decoder = Fsst16Decoder.FromSymbolTable(table);

        // Bytes the trainer never saw still round-trip: built tables cover all 256 single bytes.
        var unseen = new byte[512];
        for (int i = 0; i < unseen.Length; i++) unseen[i] = (byte)((i * 37) & 0xFF);

        var compressed = Fsst16Encoder.Compress(table, unseen);
        Assert.Equal(unseen, decoder.Decompress(compressed));
    }

    [Fact]
    public void RoundTrip_Batch()
    {
        byte[][] rows =
        [
            Encoding.UTF8.GetBytes("hello world"),
            [],
            Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"),
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("a"),
        ];

        var table = Fsst16Encoder.BuildSymbolTable(rows);
        var decoder = Fsst16Decoder.FromSymbolTable(table);

        var (data, lengths) = Fsst16Encoder.CompressBatch(table, rows);
        var dst = new byte[Fsst16Decoder.MaxDecompressedLength(data.Length)];
        var offsets = new int[rows.Length + 1];

        Assert.True(decoder.TryDecompressBatch(data, lengths, dst, offsets, out int totalWritten));
        Assert.Equal(0, offsets[0]);
        Assert.Equal(totalWritten, offsets[rows.Length]);

        for (int i = 0; i < rows.Length; i++)
        {
            var item = new byte[offsets[i + 1] - offsets[i]];
            Buffer.BlockCopy(dst, offsets[i], item, 0, item.Length);
            Assert.Equal(rows[i], item);
        }
    }

    [Fact]
    public void RoundTrip_ThroughExportRawAndFromSymbols()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox ", 300)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        var lengths = new byte[table.SymbolCount];
        var values = new byte[table.SymbolCount * 16];
        table.ExportRaw(lengths, values);

        var decoder = Fsst16Decoder.FromSymbols(lengths, values);
        var compressed = Fsst16Encoder.Compress(table, data);

        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void Compress_RepeatedPattern_BeatsRawSize()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefghijklmnop", 500)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);
        var compressed = Fsst16Encoder.Compress(table, data);

        Assert.True(compressed.Length < data.Length,
            $"Compressed {compressed.Length} should be < original {data.Length}");
    }
}
