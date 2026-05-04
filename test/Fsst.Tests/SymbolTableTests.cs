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

    [Fact]
    public void SymbolTable_SymbolCount_NeverIncludesEscapeCode()
    {
        // FSST8 reserves code 255 as the escape code, so SymbolCount must always be <= 255.
        // Feed the encoder enough variety to push the table toward saturation, then check
        // the count never crosses into the escape slot. (If the MaxSymbols cap regressed to
        // 256, this would catch it.)
        var rng = new Random(0);
        var data = new byte[256 * 1024];
        rng.NextBytes(data);

        var table = FsstEncoder.BuildSymbolTable(new[] { data });
        Assert.True(
            table.SymbolCount <= 255,
            $"SymbolCount must exclude the escape code (255); got {table.SymbolCount}.");
    }

    [Fact]
    public void SymbolTable_ExportRaw_RoundTripsThroughFsstDecoderFromSymbols()
    {
        var data = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 200)));
        var table = FsstEncoder.BuildSymbolTable(new[] { data });

        var lengths = new byte[table.SymbolCount];
        var packed = new byte[table.SymbolCount * 8];
        table.ExportRaw(lengths, packed);

        var decoder = FsstDecoder.FromSymbols(lengths, packed);
        var compressed = FsstEncoder.Compress(table, data);
        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void SymbolTable_ExportRaw_ThrowsOnTooSmallLengthsBuffer()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)));
        var table = FsstEncoder.BuildSymbolTable(new[] { data });
        Assert.True(table.SymbolCount > 0, "test precondition: encoder produced at least one symbol");

        Assert.Throws<ArgumentException>(() =>
            table.ExportRaw(new byte[table.SymbolCount - 1], new byte[table.SymbolCount * 8]));
    }

    [Fact]
    public void SymbolTable_ExportRaw_ThrowsOnTooSmallPackedValuesBuffer()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)));
        var table = FsstEncoder.BuildSymbolTable(new[] { data });
        Assert.True(table.SymbolCount > 0, "test precondition: encoder produced at least one symbol");

        Assert.Throws<ArgumentException>(() =>
            table.ExportRaw(new byte[table.SymbolCount], new byte[table.SymbolCount * 8 - 1]));
    }

    [Fact]
    public void SymbolMap_SymbolCount_ExcludesImplicitSingleByteCodes()
    {
        // FSST12's 256 single-byte codes (0..255) are implicit and not part of SymbolCount.
        var map = new SymbolMap();
        Assert.Equal(0, map.SymbolCount);

        map.Add(Symbol.FromSpan("ab"u8));
        Assert.Equal(1, map.SymbolCount);
    }

    [Fact]
    public void SymbolMap_ExportRaw_WritesExpectedSymbolBytes()
    {
        var map = new SymbolMap();
        map.Add(Symbol.FromSpan("ab"u8));
        map.Add(Symbol.FromSpan("cdef"u8));

        var lengths = new byte[map.SymbolCount];
        var packed = new byte[map.SymbolCount * 8];
        map.ExportRaw(lengths, packed);

        Assert.Equal((byte)2, lengths[0]);
        Assert.Equal((byte)4, lengths[1]);

        Assert.Equal((byte)'a', packed[0]);
        Assert.Equal((byte)'b', packed[1]);
        for (int i = 2; i < 8; i++) Assert.Equal((byte)0, packed[i]);

        Assert.Equal((byte)'c', packed[8]);
        Assert.Equal((byte)'d', packed[9]);
        Assert.Equal((byte)'e', packed[10]);
        Assert.Equal((byte)'f', packed[11]);
        for (int i = 12; i < 16; i++) Assert.Equal((byte)0, packed[i]);
    }

    [Fact]
    public void SymbolMap_ExportRaw_ThrowsOnTooSmallBuffers()
    {
        var map = new SymbolMap();
        map.Add(Symbol.FromSpan("ab"u8));

        Assert.Throws<ArgumentException>(() =>
            map.ExportRaw(new byte[0], new byte[8]));
        Assert.Throws<ArgumentException>(() =>
            map.ExportRaw(new byte[1], new byte[7]));
    }
}
