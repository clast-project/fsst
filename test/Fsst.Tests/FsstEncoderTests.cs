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

    [Fact(Skip = "Blocked on #5, a separate defect: Symbol.FromSpan/FromPointer build the probe " +
                 "with SetCodeLen(CodeMax), and CodeMax (512) masks to code 0 rather than to " +
                 "CodeMask (511) as cwida uses. The probe's code must be the maximum for the " +
                 "h.Icl <= s.Icl ordering test to reduce to a length comparison; at 0 it also " +
                 "requires the stored code to be 0 whenever lengths are equal. Un-skip once fixed.")]
    public void Compress_PrefersLongerSymbolOverLength2Prefix()
    {
        var table = new SymbolTable();
        Assert.True(table.Add(Symbol.FromSpan("ab"u8)));
        Assert.True(table.Add(Symbol.FromSpan("abcd"u8)));
        table.Finalize(false);

        // The 4-byte symbol must still win; reaching ShortCodes must not shadow the hash table.
        // Fails today only because the input is exactly 4 bytes, so the probe length equals the
        // stored symbol's and #5's zeroed code field decides the comparison.
        var compressed = FsstEncoder.Compress(table, Encoding.UTF8.GetBytes("abcd"));

        Assert.Single(compressed);
        Assert.Equal(Encoding.UTF8.GetBytes("abcd"), FsstDecoder.FromSymbolTable(table).Decompress(compressed));
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
