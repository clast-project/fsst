// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Fsst.Tests;

public class Fsst12DecoderTests
{
    [Fact]
    public void Decompress_EmptyInput_ReturnsEmpty()
    {
        var decoder = Fsst12Decoder.FromSymbolMap(new SymbolMap());
        var result = decoder.Decompress([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Decompress_SingleCode_TwoBytes()
    {
        var decoder = Fsst12Decoder.FromSymbolMap(new SymbolMap());
        // Code 65 ('A') encoded as 2 bytes
        byte[] compressed = [65, 0x00]; // low=65, high nibble=0
        var result = decoder.Decompress(compressed);
        Assert.Single(result);
        Assert.Equal((byte)'A', result[0]);
    }

    [Fact]
    public void Decompress_TwoCodes_ThreeBytes()
    {
        var decoder = Fsst12Decoder.FromSymbolMap(new SymbolMap());
        // Codes 65 ('A') and 66 ('B') packed in 3 bytes
        // code1=65=0x041, code2=66=0x042
        // byte0 = 0x41, byte1 = (0x0 | (0x2 << 4)) = 0x20, byte2 = 0x04
        byte[] compressed = [0x41, 0x20, 0x04];
        var result = decoder.Decompress(compressed);
        Assert.Equal(2, result.Length);
        Assert.Equal((byte)'A', result[0]);
        Assert.Equal((byte)'B', result[1]);
    }

    [Fact]
    public void Decompress_WithMultiByteSymbol()
    {
        var map = new SymbolMap();
        var sym = Symbol.FromSpan("xy"u8);
        map.Add(sym);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        // Code 256 (first real symbol)
        // 256 = 0x100 → byte0=0x00, byte1=0x01 (only low nibble)
        byte[] compressed = [0x00, 0x01];
        var result = decoder.Decompress(compressed);
        Assert.Equal(2, result.Length);
        Assert.Equal((byte)'x', result[0]);
        Assert.Equal((byte)'y', result[1]);
    }
}
