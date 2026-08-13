// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class Fsst12EncoderTests
{
    [Fact]
    public void BuildSymbolTable_EmptyInput_ReturnsEmptyMap()
    {
        var map = Fsst12Encoder.BuildSymbolTable([]);
        Assert.Equal(0, map.NSymbols);
    }

    [Fact]
    public void BuildSymbolTable_RepeatedData_ProducesSymbols()
    {
        var repeated = string.Concat(Enumerable.Repeat("hello world ", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var map = Fsst12Encoder.BuildSymbolTable(input);
        Assert.True(map.NSymbols > 0);
    }

    [Fact]
    public void Compress_EmptyInput_ReturnsEmpty()
    {
        var map = new SymbolMap();
        var result = Fsst12Encoder.Compress(map, []);
        Assert.Empty(result);
    }

    [Fact]
    public void Compress_SingleByte_Produces2Bytes()
    {
        var map = new SymbolMap();
        // Single byte 'A' (code 65) → 1 code → 2 bytes tail
        var result = Fsst12Encoder.Compress(map, [(byte)'A']);
        Assert.Equal(2, result.Length);

        // Verify the code is 65
        int code = result[0] | ((result[1] & 0x0F) << 8);
        Assert.Equal(65, code);
    }

    [Fact]
    public void Compress_TwoBytes_Produces3Bytes()
    {
        var map = new SymbolMap();
        // Two single-byte codes → 1 pair → 3 bytes
        var result = Fsst12Encoder.Compress(map, [(byte)'A', (byte)'B']);
        Assert.Equal(3, result.Length);

        // Verify codes
        int code1 = result[0] | ((result[1] & 0x0F) << 8);
        int code2 = (result[1] >> 4) | (result[2] << 4);
        Assert.Equal(65, code1); // 'A'
        Assert.Equal(66, code2); // 'B'
    }

    [Fact]
    public void Compress_MatchesSymbolAtCodeAboveTheFsst8CodeSpace()
    {
        // Regression for #5, FSST12 side. FSST12 assigns codes up to 4095, so the probe code has to
        // be the full 12-bit maximum -- Symbol.CodeMask (511) would leave everything above code 511
        // unreachable whenever the probe ties it on length.
        var map = new SymbolMap();

        // Burn codes with 2-byte symbols: those live in ShortCodes, so unlike 3+ byte symbols they
        // cannot collide with each other in the lossy hash. High bytes keep them clear of the target.
        for (int i = 0; i < 400; i++)
            Assert.True(map.Add(Symbol.FromSpan([(byte)(0x80 + i / 20), (byte)(0x80 + i % 20)])));

        var target = "abcdefgh"u8.ToArray();
        Assert.True(map.Add(Symbol.FromSpan(target)));

        int code = SymbolMap.CodeBase12 + map.NSymbols - 1;
        Assert.True(code > Symbol.CodeMask, $"expected a code above {Symbol.CodeMask}, got {code}");

        var compressed = Fsst12Encoder.Compress(map, target);

        Assert.Equal(2, compressed.Length); // one 12-bit code, padded to 2 bytes
        Assert.Equal(target, Fsst12Decoder.FromSymbolMap(map).Decompress(compressed));
    }

    [Fact]
    public void Compress_WithSymbols_Compresses()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var map = Fsst12Encoder.BuildSymbolTable(input);
        var data = Encoding.UTF8.GetBytes(repeated);
        var compressed = Fsst12Encoder.Compress(map, data);

        // 12-bit codes: 1.5 bytes per code. Should compress well.
        Assert.True(compressed.Length < data.Length,
            $"Compressed {compressed.Length} should be < original {data.Length}");
    }
}
