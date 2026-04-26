// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Clast.Fsst.Tests;

public class FsstSerializerTests
{
    [Fact]
    public void Fsst8_ExportImport_RoundTrips()
    {
        var repeated = string.Concat(Enumerable.Repeat("hello world ", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var table = FsstEncoder.BuildSymbolTable(input);

        var exported = FsstSerializer.ExportFsst8(table);
        Assert.True(exported.Length > 0);

        var imported = FsstSerializer.ImportFsst8(exported);
        Assert.Equal(table.NSymbols, imported.NSymbols);
    }

    [Fact]
    public void Fsst8_ExportImport_DecompressesDataCompressedWithSourceTable()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("test data here ", 200)));
        var input = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(input);

        // Compress with the original table — what Lance would write to disk alongside the symbol table.
        var compressed = FsstEncoder.Compress(table, data);

        // Round-trip the table through the cwida format and decompress with the imported one.
        var exported = FsstSerializer.ExportFsst8(table);
        var imported = FsstSerializer.ImportFsst8(exported);
        var decoder = FsstDecoder.FromSymbolTable(imported);

        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void Fsst8_ExportImport_ReimportedTableReencodesIdentically()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("test data here ", 200)));
        var table = FsstEncoder.BuildSymbolTable(new[] { data });
        var imported = FsstSerializer.ImportFsst8(FsstSerializer.ExportFsst8(table));

        Assert.Equal(FsstEncoder.Compress(table, data), FsstEncoder.Compress(imported, data));
    }

    [Fact]
    public void Fsst8_Export_HasCwidaHeaderLayout()
    {
        var table = FsstEncoder.BuildSymbolTable(new[]
        {
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)))
        });

        var exported = FsstSerializer.ExportFsst8(table);
        Assert.True(exported.Length >= 17);

        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(exported);
        Assert.Equal(20190218UL, version >> 32);
        Assert.Equal(1UL, version & 0xFF);                              // FSST_ENDIAN_MARKER
        Assert.Equal((ulong)table.NSymbols, (version >> 8) & 0xFF);
        Assert.Equal((ulong)table.SuffixLim, (version >> 24) & 0xFF);
        Assert.Equal((ulong)table.Terminator, (version >> 16) & 0xFF);
        Assert.Equal(table.ZeroTerminated ? 1 : 0, exported[8] & 1);
        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)table.LenHisto[i], exported[9 + i]);
    }

    [Fact]
    public void Fsst8_ImportKnownCwidaPayload_DecodesEscapedAndSymbolBytes()
    {
        // Hand-crafted cwida payload with two real symbols: "ab" (code 0) and "cd" (code 1).
        // Both are length-2 with non-overlapping prefixes, so suffixLim == nSymbols == 2.
        var payload = new byte[17 + 4];
        ulong version = (20190218UL << 32) | (2UL << 24) | (0UL << 16) | (2UL << 8) | 1UL;
        BinaryPrimitives.WriteUInt64LittleEndian(payload, version);
        payload[8] = 0;                            // not zeroTerminated
        payload[9 + 1] = 2;                        // lenHisto[1] (count of length-2 symbols) = 2
        // Symbol bytes: "ab" then "cd".
        payload[17] = (byte)'a'; payload[18] = (byte)'b';
        payload[19] = (byte)'c'; payload[20] = (byte)'d';

        var imported = FsstSerializer.ImportFsst8(payload);
        Assert.Equal(2, imported.NSymbols);

        var decoder = FsstDecoder.FromSymbolTable(imported);

        // Code 0 -> "ab", code 1 -> "cd", escape (255 0x21) -> "!"
        byte[] compressed = [0x00, 0x01, 0xFF, (byte)'!'];
        Assert.Equal(Encoding.UTF8.GetBytes("abcd!"), decoder.Decompress(compressed));
    }

    [Fact]
    public void Fsst8_ImportRejectsTruncatedHeader()
    {
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst8(new byte[16]));
    }

    [Fact]
    public void Fsst8_ImportRejectsWrongVersionMagic()
    {
        var payload = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (12345UL << 32) | 1UL);
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst8(payload));
    }

    [Fact]
    public void Fsst8_ImportRejectsWrongEndianMarker()
    {
        var payload = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 20190218UL << 32); // endian marker = 0
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst8(payload));
    }

    [Fact]
    public void Fsst8_FromSymbols_DecodesHandCraftedSlots()
    {
        // Two real symbols at codes 0 and 1: "ab" and "cd". Slot 255 is unused.
        // This is the shape Lance hands us after parsing its TSSF block.
        var lengths = new byte[256];
        var packed = new byte[256 * 8];
        lengths[0] = 2; packed[0] = (byte)'a'; packed[1] = (byte)'b';
        lengths[1] = 2; packed[8] = (byte)'c'; packed[9] = (byte)'d';

        var decoder = FsstDecoder.FromSymbols(lengths, packed);

        byte[] compressed = [0x00, 0x01, 0xFF, (byte)'!'];
        Assert.Equal(Encoding.UTF8.GetBytes("abcd!"), decoder.Decompress(compressed));
    }

    [Fact]
    public void Fsst8_FromSymbols_MatchesFromSymbolTableForRealCorpus()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("test data here ", 200)));
        var table = FsstEncoder.BuildSymbolTable(new[] { data });
        var compressed = FsstEncoder.Compress(table, data);

        // Project the finalized table into per-code lengths/values, then rebuild via FromSymbols.
        var lengths = new byte[255];
        var packed = new byte[255 * 8];
        for (int i = 0; i < table.NSymbols && i < 255; i++)
        {
            var sym = table.Symbols[i];
            lengths[i] = (byte)sym.Length();
            BinaryPrimitives.WriteUInt64LittleEndian(packed.AsSpan(i * 8, 8), sym.Val);
        }

        var decoder = FsstDecoder.FromSymbols(lengths, packed);
        Assert.Equal(data, decoder.Decompress(compressed));
    }

    [Fact]
    public void Fsst8_FromSymbols_RejectsMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() => FsstDecoder.FromSymbols(new byte[2], new byte[15]));
    }

    [Fact]
    public void Fsst8_FromSymbols_RejectsTooManySlots()
    {
        Assert.Throws<ArgumentException>(() => FsstDecoder.FromSymbols(new byte[257], new byte[257 * 8]));
    }

    [Fact]
    public void Fsst8_FromSymbols_RejectsEscapeSlotInUse()
    {
        var lengths = new byte[256];
        lengths[255] = 1;
        Assert.Throws<ArgumentException>(() => FsstDecoder.FromSymbols(lengths, new byte[256 * 8]));
    }

    [Fact]
    public void Fsst8_FromSymbols_RejectsOverlongSymbol()
    {
        var lengths = new byte[1] { 9 };
        Assert.Throws<ArgumentException>(() => FsstDecoder.FromSymbols(lengths, new byte[8]));
    }

    [Fact]
    public void Fsst12_ExportImport_RoundTrips()
    {
        var repeated = string.Concat(Enumerable.Repeat("hello world ", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var map = Fsst12Encoder.BuildSymbolTable(input);

        var exported = FsstSerializer.ExportFsst12(map);
        Assert.True(exported.Length > 0);

        var imported = FsstSerializer.ImportFsst12(exported);
        Assert.Equal(map.NSymbols, imported.NSymbols);
    }

    [Fact]
    public void Fsst12_ExportImport_CompressDecompressStillWorks()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("test data here ", 200)));
        var input = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(input);

        var exported = FsstSerializer.ExportFsst12(map);
        var imported = FsstSerializer.ImportFsst12(exported);
        var decoder = Fsst12Decoder.FromSymbolMap(imported);

        var compressed = Fsst12Encoder.Compress(imported, data);
        var decompressed = decoder.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_ImportInvalidData_Throws()
    {
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst12(new byte[5]));
    }
}
