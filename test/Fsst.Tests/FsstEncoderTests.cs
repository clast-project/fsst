// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class FsstEncoderTests
{
    [Fact]
    public void Compress_UsesLength2Symbols()
    {
        // Regression for #2. Finalize renumbers real codes to 0..254, so FindLongestSymbol's
        // ShortCodes test could not be a code-vs-CodeBase comparison: length-2 symbols were in the
        // table but unreachable, and their bytes were escaped instead — 2x expansion where 2x
        // compression was available.
        var table = new SymbolTable();
        Assert.True(table.Add(Symbol.FromSpan("ab"u8)));
        table.Finalize(false);

        var compressed = FsstEncoder.Compress(table, Encoding.UTF8.GetBytes("abab"));

        Assert.Equal(2, compressed.Length);
        Assert.Equal(Encoding.UTF8.GetBytes("abab"), FsstDecoder.FromSymbolTable(table).Decompress(compressed));
    }

    [Fact]
    public void Compress_Length2SymbolsCoexistWithEscapesAndLongerSymbols()
    {
        var table = new SymbolTable();
        Assert.True(table.Add(Symbol.FromSpan("ab"u8)));    // length 2 -> ShortCodes
        Assert.True(table.Add(Symbol.FromSpan("xyz"u8)));   // length 3 -> hash table
        Assert.True(table.Add(Symbol.FromByte((byte)'q', 0)));
        table.Finalize(false);

        var data = Encoding.UTF8.GetBytes("abxyzq!ab");
        var compressed = FsstEncoder.Compress(table, data);

        // ab, xyz, q, escape '!', ab  ->  4 codes + 1 escape pair = 6 bytes.
        Assert.Equal(6, compressed.Length);
        Assert.Equal(data, FsstDecoder.FromSymbolTable(table).Decompress(compressed));
    }

    [Fact]
    public void Compress_PrefersLongerSymbolOverLength2Prefix()
    {
        var table = new SymbolTable();
        Assert.True(table.Add(Symbol.FromSpan("ab"u8)));
        Assert.True(table.Add(Symbol.FromSpan("abcd"u8)));
        table.Finalize(false);

        // The 4-byte symbol must win; reaching ShortCodes must not shadow the hash table. The input
        // is exactly 4 bytes, so the probe's length equals the stored symbol's and the comparison
        // falls through to the code field — regression for #5.
        var compressed = FsstEncoder.Compress(table, Encoding.UTF8.GetBytes("abcd"));

        Assert.Single(compressed);
        Assert.Equal(Encoding.UTF8.GetBytes("abcd"), FsstDecoder.FromSymbolTable(table).Decompress(compressed));
    }

    [Fact]
    public void Compress_MatchesSymbolWhoseLengthEqualsRemainingInput()
    {
        // Regression for #5 across every length. The probe is min(remaining, 8) bytes, so a symbol
        // sitting at the end of a value always ties the probe on length. Each symbol below is added
        // second so it lands at a nonzero code, which is what the zeroed probe code used to require.
        for (int len = 2; len <= Symbol.MaxLength; len++)
        {
            var symbol = Encoding.UTF8.GetBytes(new string('x', len - 1) + "y");

            var table = new SymbolTable();
            Assert.True(table.Add(Symbol.FromSpan("zz"u8)));            // occupies a low code
            Assert.True(table.Add(Symbol.FromSpan(symbol)));
            table.Finalize(false);

            var compressed = FsstEncoder.Compress(table, symbol);

            Assert.True(compressed.Length == 1, $"length-{len} symbol took {compressed.Length} bytes, expected 1 code");
            Assert.Equal(symbol, FsstDecoder.FromSymbolTable(table).Decompress(compressed));
        }
    }

    [Fact]
    public void Compress_MatchesLength8SymbolMidStream()
    {
        // Mid-stream the probe is always 8 bytes, so length-8 symbols tie on length every time.
        var table = new SymbolTable();
        Assert.True(table.Add(Symbol.FromSpan("zz"u8)));
        Assert.True(table.Add(Symbol.FromSpan("abcdefgh"u8)));
        table.Finalize(false);

        var data = Encoding.UTF8.GetBytes("abcdefghabcdefghabcdefgh");
        var compressed = FsstEncoder.Compress(table, data);

        Assert.Equal(3, compressed.Length);
        Assert.Equal(data, FsstDecoder.FromSymbolTable(table).Decompress(compressed));
    }

    [Fact]
    public void BuildSymbolTable_TrainsLength8Symbols()
    {
        // The trainer parses with the same lookup, so #5 also stopped long symbols from ever being
        // built: unusable symbols never appear in the parse, so pairs never grow from them.
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 500)));
        var table = FsstEncoder.BuildSymbolTable([data]);

        Assert.True(table.LenHisto[7] > 0, $"expected length-8 symbols, got histogram [{string.Join(",", table.LenHisto)}]");

        var compressed = FsstEncoder.Compress(table, data);
        Assert.True(compressed.Length * 4 < data.Length,
            $"expected better than 4x, got {(double)data.Length / compressed.Length:F2}x");
    }

    [Fact]
    public void BuildSymbolTable_EmptyInput_ReturnsEmptyTable()
    {
        var table = FsstEncoder.BuildSymbolTable([]);
        Assert.Equal(0, table.NSymbols);
    }

    [Fact]
    public void BuildSymbolTable_SingleString_ProducesSymbols()
    {
        var input = new[] { Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog") };
        var table = FsstEncoder.BuildSymbolTable(input);
        Assert.True(table.NSymbols > 0);
    }

    [Fact]
    public void BuildSymbolTable_RepeatedPatterns_ProducesMoreSymbols()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdef", 100));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var table = FsstEncoder.BuildSymbolTable(input);
        Assert.True(table.NSymbols > 0);
    }

    [Fact]
    public void Compress_EmptyInput_ReturnsEmpty()
    {
        var table = new SymbolTable();
        var result = FsstEncoder.Compress(table, []);
        Assert.Empty(result);
    }

    [Fact]
    public void Compress_SingleByte_ProducesEscapedOutput()
    {
        var table = new SymbolTable();
        table.Finalize(false);
        var result = FsstEncoder.Compress(table, [0x42]);
        // With no real symbols, should escape: [255, 0x42]
        Assert.Equal(2, result.Length);
        Assert.Equal(255, result[0]);
        Assert.Equal(0x42, result[1]);
    }

    [Fact]
    public void Compress_WithSymbols_ProducesSmallerOutput()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var table = FsstEncoder.BuildSymbolTable(input);
        var data = Encoding.UTF8.GetBytes(repeated);
        var compressed = FsstEncoder.Compress(table, data);

        // Compressed should be smaller than original
        Assert.True(compressed.Length < data.Length,
            $"Compressed {compressed.Length} should be < original {data.Length}");
    }

    [Fact]
    public void CompressBatch_MultipleStrings()
    {
        var strings = new[]
        {
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("hello there"),
            Encoding.UTF8.GetBytes("world hello"),
        };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var (data, lengths) = FsstEncoder.CompressBatch(table, strings);

        Assert.Equal(3, lengths.Length);
        Assert.Equal(data.Length, lengths.Sum());
    }
}
