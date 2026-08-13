// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.



namespace Clast.Fsst.Tests;

public class Fsst16DecoderTests
{
    /// <summary>Build the raw symbol arrays <see cref="Fsst16Decoder.FromSymbols"/> expects.</summary>
    private static (byte[] Lengths, byte[] Values) Pack(params byte[][] symbols)
    {
        var lengths = new byte[symbols.Length];
        var values = new byte[symbols.Length * 16];
        for (int i = 0; i < symbols.Length; i++)
        {
            lengths[i] = (byte)symbols[i].Length;
            symbols[i].CopyTo(values, i * 16);
        }
        return (lengths, values);
    }

    [Fact]
    public void Decompress_EmptyInput_ReturnsEmpty()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        Assert.Empty(Fsst16Decoder.FromSymbols(lengths, values).Decompress([]));
    }

    [Fact]
    public void Decompress_ReadsCodesLittleEndian()
    {
        // Code 0 = "ab", code 1 = "cd", code 258 = "ef".
        var symbols = new byte[259][];
        for (int i = 0; i < 259; i++) symbols[i] = [];
        symbols[0] = "ab"u8.ToArray();
        symbols[1] = "cd"u8.ToArray();
        symbols[258] = "ef"u8.ToArray();

        var (lengths, values) = Pack(symbols);
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        // 0x0000, 0x0001, 0x0102
        byte[] compressed = [0x00, 0x00, 0x01, 0x00, 0x02, 0x01];
        Assert.Equal("abcdef"u8.ToArray(), decoder.Decompress(compressed));
    }

    [Fact]
    public void Decompress_SixteenByteSymbol()
    {
        var symbol = "0123456789abcdef"u8.ToArray();
        var (lengths, values) = Pack(symbol);
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.Equal(symbol, decoder.Decompress([0x00, 0x00]));
    }

    [Fact]
    public void Decompress_Escape_EmitsLiteralByte()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        byte[] compressed = [0x00, 0x00, 0xFF, 0xFF, 0x5A, 0x00, 0x00];
        Assert.Equal("abZab"u8.ToArray(), decoder.Decompress(compressed));
    }

    [Fact]
    public void Decompress_DanglingEscapeIsIgnored()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.Equal("ab"u8.ToArray(), decoder.Decompress([0x00, 0x00, 0xFF, 0xFF]));
    }

    [Fact]
    public void Decompress_TrailingOddByteIsIgnored()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.Equal("ab"u8.ToArray(), decoder.Decompress([0x00, 0x00, 0x07]));
    }

    [Fact]
    public void Decompress_UnusedCodeEmitsNothing()
    {
        var (lengths, values) = Pack("ab"u8.ToArray(), []);
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.Equal("ab"u8.ToArray(), decoder.Decompress([0x01, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void FromSymbols_RejectsMalformedInput()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());

        Assert.Throws<ArgumentException>(() => Fsst16Decoder.FromSymbols(lengths, new byte[8]));
        Assert.Throws<ArgumentException>(() => Fsst16Decoder.FromSymbols(new byte[65537], new byte[65537 * 16]));
        Assert.Throws<ArgumentException>(() => Fsst16Decoder.FromSymbols([17], new byte[16]));

        // Slot 65,535 is the escape code and must stay empty.
        var full = new byte[65536];
        full[65535] = 1;
        Assert.Throws<ArgumentException>(() => Fsst16Decoder.FromSymbols(full, new byte[65536 * 16]));

        Assert.Equal(2, Fsst16Decoder.FromSymbols(lengths, values).Decompress([0x00, 0x00]).Length);
    }

    [Fact]
    public void MaxDecompressedLength_RoundsCodesUp()
    {
        Assert.Equal(0, Fsst16Decoder.MaxDecompressedLength(0));
        Assert.Equal(16, Fsst16Decoder.MaxDecompressedLength(1));
        Assert.Equal(16, Fsst16Decoder.MaxDecompressedLength(2));
        Assert.Equal(32, Fsst16Decoder.MaxDecompressedLength(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Decoder.MaxDecompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst16Decoder.MaxDecompressedLength(int.MaxValue));
    }

    [Fact]
    public void TryDecompress_DestinationTooSmall_ReturnsFalse()
    {
        var (lengths, values) = Pack("0123456789abcdef"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.False(decoder.TryDecompress([0x00, 0x00], new byte[15], out int written));
        Assert.Equal(0, written);

        // Exactly-sized destination takes the careful (non-16-byte-write) path.
        Assert.True(decoder.TryDecompress([0x00, 0x00], new byte[16], out written));
        Assert.Equal(16, written);
    }

    [Fact]
    public void TryDecompress_CarefulPath_WritesSymbolTailCorrectly()
    {
        var (lengths, values) = Pack("abcdefghij"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        var dst = new byte[10];
        Assert.True(decoder.TryDecompress([0x00, 0x00], dst, out int written));
        Assert.Equal(10, written);
        Assert.Equal("abcdefghij"u8.ToArray(), dst);
    }

    [Fact]
    public void TryDecompress_EscapeWithFullDestination_ReturnsFalse()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.False(decoder.TryDecompress([0x00, 0x00, 0xFF, 0xFF, 0x5A], new byte[2], out _));
    }

    [Fact]
    public void Decompress_ToBufferWriter_MatchesArrayOverload()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        byte[] compressed = [0x00, 0x00, 0x00, 0x00];
        var writer = new ListBufferWriter();
        decoder.Decompress(compressed, writer);

        Assert.Equal("abab"u8.ToArray(), writer.WrittenSpan.ToArray());
        Assert.Equal("abab", decoder.DecompressString(compressed));
        Assert.Equal(string.Empty, decoder.DecompressString([]));
    }

    [Fact]
    public void TryDecompressBatch_WrongOffsetLength_ReturnsFalse()
    {
        var (lengths, values) = Pack("ab"u8.ToArray());
        var decoder = Fsst16Decoder.FromSymbols(lengths, values);

        Assert.False(decoder.TryDecompressBatch([0x00, 0x00], [2], new byte[16], new int[1], out _));
    }
}
