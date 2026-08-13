// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class FsstRoundTripTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void Fsst8_RoundTrip_Identity(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_RepeatedPattern()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var data = Encoding.UTF8.GetBytes(repeated);
        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_AllByteValues()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;
        // Repeat to give the encoder something to work with
        var repeated = new byte[256 * 10];
        for (int i = 0; i < 10; i++) data.CopyTo(repeated.AsSpan(i * 256));

        var strings = new[] { repeated };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, repeated);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(repeated, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_BatchMultipleStrings()
    {
        var strings = new[]
        {
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("foo bar baz"),
            Encoding.UTF8.GetBytes("the quick brown fox"),
            Encoding.UTF8.GetBytes("aaaaaaaaaa"),
        };

        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var (compressedData, lengths) = FsstEncoder.CompressBatch(table, strings);
        var decompressed = decoder.DecompressBatch(compressedData, lengths);

        Assert.Equal(strings.Length, decompressed.Length);
        for (int i = 0; i < strings.Length; i++)
            Assert.Equal(strings[i], decompressed[i]);
    }

    [Fact]
    public void Fsst8_RoundTrip_EmptyStrings()
    {
        byte[][] strings = [[], []];
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var (compressedData, lengths) = FsstEncoder.CompressBatch(table, strings);
        var decompressed = decoder.DecompressBatch(compressedData, lengths);

        Assert.Equal(2, decompressed.Length);
        Assert.Empty(decompressed[0]);
        Assert.Empty(decompressed[1]);
    }

    [Fact]
    public void Fsst8_RoundTrip_LongString()
    {
        var rng = new Random(42);
        var data = new byte[100_000];
        // Mix of repeated patterns and random data
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 20 < 10)
                data[i] = (byte)('a' + (i % 10));
            else
                data[i] = (byte)rng.Next(256);
        }

        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public void Fsst12_RoundTrip_Identity(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_RoundTrip_RepeatedPattern()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var data = Encoding.UTF8.GetBytes(repeated);
        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_RoundTrip_AllByteValues()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;

        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_TrainedOnRecordLikeValues()
    {
        // Regression for #16, reported as "0.3.0 corrupts FSST8 on write". Training on this corpus
        // produces a table holding both a suffixed length-2 symbol and length-3 symbols, which
        // Finalize gave overlapping codes — 38 of the 42 values came back altered.
        var values = new byte[42][];
        for (int i = 0; i < values.Length; i++)
            values[i] = Encoding.UTF8.GetBytes($"record-{i}-payload-{i % 11}");

        var table = FsstEncoder.BuildSymbolTable(values);
        var decoder = FsstDecoder.FromSymbolTable(table);

        foreach (var value in values)
            Assert.Equal(value, decoder.Decompress(FsstEncoder.Compress(table, value)));
    }

    [Fact]
    public void Fsst8_RoundTrip_RandomCorporaOverSmallAlphabets()
    {
        // Small alphabets make near-duplicate short symbols likely, which is what drives symbols
        // into the suffixed length-2 class that #16 mis-numbered.
        var rng = new Random(12345);
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_/.:";

        for (int trial = 0; trial < 200; trial++)
        {
            int alphabetSize = 2 + rng.Next(Alphabet.Length - 1);
            var values = new byte[1 + rng.Next(60)][];
            for (int i = 0; i < values.Length; i++)
            {
                var chars = new char[rng.Next(40)];
                for (int j = 0; j < chars.Length; j++)
                    chars[j] = Alphabet[rng.Next(alphabetSize)];
                values[i] = Encoding.UTF8.GetBytes(new string(chars));
            }

            var table = FsstEncoder.BuildSymbolTable(values);
            var decoder = FsstDecoder.FromSymbolTable(table);

            foreach (var value in values)
                Assert.Equal(value, decoder.Decompress(FsstEncoder.Compress(table, value)));
        }
    }

    [Fact]
    public void Fsst12_RoundTrip_LongString()
    {
        var rng = new Random(42);
        var data = new byte[50_000];
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 20 < 10)
                data[i] = (byte)('a' + (i % 10));
            else
                data[i] = (byte)rng.Next(256);
        }

        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed);

        Assert.Equal(data, decompressed);
    }
}
