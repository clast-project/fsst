// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Clast.Fsst.Tests;

public class SymbolTable16Tests
{
    [Fact]
    public void NewTable_HasNoSymbols()
    {
        var table = new SymbolTable16(16);
        Assert.Equal(0, table.SymbolCount);
        Assert.Equal(16, table.MaxSymbolLength);
    }

    [Fact]
    public void Add_AssignsSequentialCodes()
    {
        var table = new SymbolTable16(16);

        Assert.True(table.Add(Symbol16.FromSpan("ab"u8)));
        Assert.True(table.Add(Symbol16.FromSpan("cde"u8)));
        Assert.Equal(2, table.SymbolCount);
        Assert.Equal(0, table.Symbols[0].Code());
        Assert.Equal(1, table.Symbols[1].Code());
    }

    [Fact]
    public void Add_RejectsDuplicatesAndOverlongSymbols()
    {
        var table = new SymbolTable16(8);

        Assert.True(table.Add(Symbol16.FromByte((byte)'a')));
        Assert.False(table.Add(Symbol16.FromByte((byte)'a')));

        Assert.True(table.Add(Symbol16.FromSpan("xy"u8)));
        Assert.False(table.Add(Symbol16.FromSpan("xy"u8)));

        Assert.False(table.Add(Symbol16.FromSpan("123456789"u8)));
        Assert.Equal(2, table.SymbolCount);
    }

    [Fact]
    public void FindLongestSymbol_PrefersLongerMatches()
    {
        var table = new SymbolTable16(16);
        table.Add(Symbol16.FromByte((byte)'a'));         // code 0
        table.Add(Symbol16.FromSpan("ab"u8));            // code 1
        table.Add(Symbol16.FromSpan("abcdefghijklmnop"u8)); // code 2

        Assert.Equal(2, Find(table, "abcdefghijklmnopqrst"u8));
        Assert.Equal(1, Find(table, "abZZ"u8));
        Assert.Equal(0, Find(table, "aZZZ"u8));
        Assert.Equal(SymbolTable16.EscCode, Find(table, "ZZZZ"u8));
    }

    [Fact]
    public void FindLongestSymbol_MatchesOnlyOnFullSymbol()
    {
        var table = new SymbolTable16(16);
        table.Add(Symbol16.FromByte((byte)'a'));
        table.Add(Symbol16.FromSpan("abcdefghijklmnop"u8));

        // The 16-byte symbol must not match a 15-byte prefix of the input.
        Assert.Equal(0, Find(table, "abcdefghijklmno"u8));
    }

    private static int Find(SymbolTable16 table, ReadOnlySpan<byte> input)
        => table.FindLongestSymbol(Symbol16.FromSpan(input));

    [Fact]
    public void ExportRaw_WritesLengthsAndSixteenBytePaddedValues()
    {
        var table = new SymbolTable16(16);
        table.Add(Symbol16.FromSpan("ab"u8));
        table.Add(Symbol16.FromSpan("0123456789abcdef"u8));

        var lengths = new byte[table.SymbolCount];
        var values = new byte[table.SymbolCount * 16];
        table.ExportRaw(lengths, values);

        Assert.Equal(new byte[] { 2, 16 }, lengths);
        Assert.Equal("ab"u8.ToArray(), values.AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[14], values.AsSpan(2, 14).ToArray()); // zero-padded to the 16-byte slot
        Assert.Equal("0123456789abcdef"u8.ToArray(), values.AsSpan(16, 16).ToArray());
    }

    [Fact]
    public void ExportRaw_RejectsUndersizedBuffers()
    {
        var table = new SymbolTable16(16);
        table.Add(Symbol16.FromSpan("ab"u8));

        Assert.Throws<ArgumentException>(() => table.ExportRaw(new byte[0], new byte[16]));
        Assert.Throws<ArgumentException>(() => table.ExportRaw(new byte[1], new byte[15]));
    }

    [Fact]
    public void ExportRaw_ValuesAreLittleEndian()
    {
        var table = new SymbolTable16(16);
        table.Add(Symbol16.FromSpan([0x01, 0x02, 0x03]));

        var values = new byte[16];
        table.ExportRaw(new byte[1], values);

        Assert.Equal(0x030201UL, BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(0, 8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(8, 8)));
    }

    [Fact]
    public void BuiltTable_CoversEverySingleByte()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)));
        var table = Fsst16Encoder.BuildSymbolTable([data]);

        var lengths = new byte[table.SymbolCount];
        table.ExportRaw(lengths, new byte[table.SymbolCount * 16]);

        // Ascending-length ordering puts the 256 single bytes first, in byte order.
        Assert.Equal(256, lengths.Count(len => len == 1));
        for (int i = 0; i < 256; i++)
            Assert.Equal(1, lengths[i]);
    }
}
