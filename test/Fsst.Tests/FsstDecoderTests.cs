// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class FsstDecoderTests
{
    private static FsstDecoder EmptyDecoder()
    {
        var table = new SymbolTable();
        table.Finalize(false);
        return FsstDecoder.FromSymbolTable(table);
    }

    [Fact]
    public void Decompress_EmptyInput_ReturnsEmpty()
    {
        var decoder = EmptyDecoder();
        var result = decoder.Decompress([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Decompress_EscapedByte_ReturnsLiteral()
    {
        var decoder = EmptyDecoder();
        // Escape code 255 followed by literal 0x42
        byte[] compressed = [255, 0x42];
        var result = decoder.Decompress(compressed);
        Assert.Single(result);
        Assert.Equal(0x42, result[0]);
    }

    [Fact]
    public void Decompress_MultipleEscapedBytes()
    {
        var decoder = EmptyDecoder();
        byte[] compressed = [255, 0x41, 255, 0x42, 255, 0x43];
        var result = decoder.Decompress(compressed);
        Assert.Equal(3, result.Length);
        Assert.Equal("ABC"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_WithSymbol_RoundTripsViaEncoder()
    {
        // Build a table with the symbol "AB", compress data containing it, and round-trip.
        var table = new SymbolTable();
        table.Add(Symbol.FromSpan("AB"u8));
        table.Finalize(false);

        var compressed = FsstEncoder.Compress(table, "ABAB"u8);
        var decoder = FsstDecoder.FromSymbolTable(table);
        var result = decoder.Decompress(compressed);

        Assert.Equal("ABAB"u8.ToArray(), result);
    }
}
