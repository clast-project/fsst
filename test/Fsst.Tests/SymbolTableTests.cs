// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

public class SymbolTableTests
{
    [Fact]
    public void Symbol_FromByte_SetsCorrectly()
    {
        var s = Symbol.FromByte(0x41, 0x41);
        Assert.Equal(1, s.Length());
        Assert.Equal(0x41, s.First());
    }

    [Fact]
    public void Symbol_FromSpan_ShortString()
    {
        var s = Symbol.FromSpan("abc"u8);
        Assert.Equal(3, s.Length());
        Assert.Equal((byte)'a', s.First());
        Assert.Equal((ushort)('a' | ('b' << 8)), s.First2());
    }

    [Fact]
    public void Symbol_FromSpan_MaxLength()
    {
        var data = "abcdefgh"u8;
        var s = Symbol.FromSpan(data);
        Assert.Equal(8, s.Length());
    }

    [Fact]
    public void Symbol_FromSpan_LongerThan8Truncates()
    {
        var data = "abcdefghij"u8;
        var s = Symbol.FromSpan(data);
        Assert.Equal(8, s.Length());
    }

    [Fact]
    public void Symbol_Concat_TwoSymbols()
    {
        var a = Symbol.FromSpan("ab"u8);
        var b = Symbol.FromSpan("cd"u8);
        var c = Symbol.Concat(a, b);
        Assert.Equal(4, c.Length());
    }

    [Fact]
    public void Symbol_Concat_TruncatesAt8()
    {
        var a = Symbol.FromSpan("abcde"u8);
        var b = Symbol.FromSpan("fghij"u8);
        var c = Symbol.Concat(a, b);
        Assert.Equal(8, c.Length());
    }

    [Fact]
    public void Symbol_SetCodeLen_RoundTrips()
    {
        var s = Symbol.FromSpan("test"u8);
        s.SetCodeLen(300, 4);
        Assert.Equal(300, s.Code());
        Assert.Equal(4, s.Length());
        Assert.Equal(32, s.IgnoredBits()); // (8-4)*8 = 32
    }

    [Fact]
    public void Symbol_Free_IsFree()
    {
        var s = Symbol.Free();
        Assert.True(s.IsFree());
    }

    [Fact]
    public void SymbolTable_Init_HasCorrectDefaults()
    {
        var table = new SymbolTable();
        Assert.Equal(0, table.NSymbols);
        Assert.False(table.ZeroTerminated);
    }

    [Fact]
    public void SymbolTable_Add_SingleByte()
    {
        var table = new SymbolTable();
        var s = Symbol.FromSpan("x"u8);
        bool added = table.Add(s);
        Assert.True(added);
        Assert.Equal(1, table.NSymbols);
    }

    [Fact]
    public void SymbolTable_Add_TwoByte()
    {
        var table = new SymbolTable();
        var s = Symbol.FromSpan("ab"u8);
        bool added = table.Add(s);
        Assert.True(added);
        Assert.Equal(1, table.NSymbols);
    }

    [Fact]
    public void SymbolTable_Add_ThreeByte_HashInsert()
    {
        var table = new SymbolTable();
        var s = Symbol.FromSpan("abc"u8);
        bool added = table.Add(s);
        Assert.True(added);
        Assert.Equal(1, table.NSymbols);
    }

    [Fact]
    public void SymbolTable_FindLongestSymbol_FindsTwoByte()
    {
        var table = new SymbolTable();
        var s = Symbol.FromSpan("ab"u8);
        table.Add(s);

        var lookup = Symbol.FromSpan("abcd"u8);
        int code = table.FindLongestSymbol(lookup);
        Assert.True(code >= Symbol.CodeBase);
        Assert.Equal(2, table.Symbols[code].Length());
    }

    [Fact]
    public void SymbolTable_FindLongestSymbol_FindsThreeByte()
    {
        var table = new SymbolTable();
        var s = Symbol.FromSpan("abc"u8);
        table.Add(s);

        var lookup = Symbol.FromSpan("abcdef"u8);
        int code = table.FindLongestSymbol(lookup);
        Assert.True(code >= Symbol.CodeBase);
        Assert.Equal(3, table.Symbols[code].Length());
    }

    [Fact]
    public void SymbolTable_FindLongestSymbol_FallsBackToSingleByte()
    {
        var table = new SymbolTable();
        var lookup = Symbol.FromSpan("x"u8);
        int code = table.FindLongestSymbol(lookup);
        Assert.True(code < Symbol.CodeBase);
    }

    [Fact]
    public void SymbolTable_Clear_ResetsSymbols()
    {
        var table = new SymbolTable();
        table.Add(Symbol.FromSpan("ab"u8));
        table.Add(Symbol.FromSpan("cd"u8));
        Assert.Equal(2, table.NSymbols);

        table.Clear();
        Assert.Equal(0, table.NSymbols);
    }

    [Fact]
    public void SymbolMap_Init_HasCorrectDefaults()
    {
        var map = new SymbolMap();
        Assert.Equal(0, map.NSymbols);
    }

    [Fact]
    public void SymbolMap_Add_TwoByte()
    {
        var map = new SymbolMap();
        var s = Symbol.FromSpan("ab"u8);
        bool added = map.Add(s);
        Assert.True(added);
        Assert.Equal(1, map.NSymbols);
    }

    [Fact]
    public void SymbolMap_Add_RejectsSingleByte()
    {
        var map = new SymbolMap();
        var s = Symbol.FromSpan("x"u8);
        bool added = map.Add(s);
        Assert.False(added); // single bytes already covered
    }

    [Fact]
    public void SymbolMap_FindLongestSymbol_FindsTwoByte()
    {
        var map = new SymbolMap();
        map.Add(Symbol.FromSpan("ab"u8));

        var lookup = Symbol.FromSpan("abcd"u8);
        int code = map.FindLongestSymbol(lookup);
        Assert.True(code >= SymbolMap.CodeBase12);
    }
}
