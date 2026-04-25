// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Fsst;

/// <summary>
/// Export/import symbol tables for both FSST8 and FSST12 variants.
/// </summary>
public static class FsstSerializer
{
    private const ulong Fsst8Version = 20190218UL;
    private const ulong Fsst12Version = 20190219UL;

    /// <summary>
    /// Export an FSST8 symbol table to bytes.
    /// Format: 8-byte version | 1-byte zeroTerminated | 8-byte lenHisto (1 byte each) |
    ///         concatenated symbol bytes (length prefix per symbol)
    /// </summary>
    public static byte[] ExportFsst8(SymbolTable table)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Version header
        writer.Write(Fsst8Version);

        // Zero terminated flag
        writer.Write(table.ZeroTerminated ? (byte)1 : (byte)0);

        // Length histogram (9 entries, write 8 bytes)
        for (int i = 0; i < 8; i++)
            writer.Write((byte)table.LenHisto[i]);

        // Number of symbols
        writer.Write((ushort)table.NSymbols);

        // Concatenated symbol data
        byte[] symBuf = new byte[8];
        for (int i = 0; i < table.NSymbols; i++)
        {
            var sym = table.Symbols[i];
            int len = sym.Length();
            writer.Write((byte)len);

            BinaryPrimitives.WriteUInt64LittleEndian(symBuf, sym.Val);
            writer.Write(symBuf, 0, len);
        }

        return ms.ToArray();
    }

    /// <summary>Import an FSST8 symbol table from bytes.</summary>
    public static SymbolTable ImportFsst8(ReadOnlySpan<byte> data)
    {
        if (data.Length < 19) // 8 + 1 + 8 + 2 minimum
            throw new ArgumentException("Invalid FSST8 serialized data");

        int pos = 0;

        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
        pos += 8;
        if (version != Fsst8Version)
            throw new ArgumentException($"Unknown FSST8 version: {version}");

        bool zeroTerminated = data[pos++] != 0;

        // Read length histogram
        Span<int> lenHisto = stackalloc int[8];
        for (int i = 0; i < 8; i++)
            lenHisto[i] = data[pos++];

        int nSymbols = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;

        var table = new SymbolTable();

        for (int i = 0; i < nSymbols; i++)
        {
            int len = data[pos++];
            var sym = Symbol.FromSpan(data.Slice(pos, len));
            pos += len;
            table.Add(sym);
        }

        table.Finalize(zeroTerminated);
        return table;
    }

    /// <summary>
    /// Export an FSST12 symbol map to bytes.
    /// Format: 8-byte version | 16-byte lenHisto (u16 each, 8 entries) |
    ///         (length byte + symbol bytes) per symbol
    /// </summary>
    public static byte[] ExportFsst12(SymbolMap map)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Version header
        writer.Write(Fsst12Version);

        // Length histogram (8 entries, u16 each)
        for (int i = 0; i < 8; i++)
            writer.Write((ushort)map.LenHisto[i]);

        // Number of symbols
        writer.Write((ushort)map.NSymbols);

        // Symbol data
        byte[] mapBuf = new byte[8];
        for (int i = SymbolMap.CodeBase12; i < SymbolMap.CodeBase12 + map.NSymbols; i++)
        {
            var sym = map.Symbols[i];
            int len = sym.Length();
            writer.Write((byte)len);

            BinaryPrimitives.WriteUInt64LittleEndian(mapBuf, sym.Val);
            writer.Write(mapBuf, 0, len);
        }

        return ms.ToArray();
    }

    /// <summary>Import an FSST12 symbol map from bytes.</summary>
    public static SymbolMap ImportFsst12(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26) // 8 + 16 + 2 minimum
            throw new ArgumentException("Invalid FSST12 serialized data");

        int pos = 0;

        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
        pos += 8;
        if (version != Fsst12Version)
            throw new ArgumentException($"Unknown FSST12 version: {version}");

        // Length histogram
        pos += 16; // skip, we rebuild from symbols

        int nSymbols = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;

        var map = new SymbolMap();

        for (int i = 0; i < nSymbols; i++)
        {
            int len = data[pos++];
            var sym = Symbol.FromSpan(data.Slice(pos, len));
            pos += len;
            map.Add(sym);
        }

        return map;
    }

    /// <summary>Create an FSST8 decoder from exported bytes.</summary>
    public static FsstDecoder ImportFsst8Decoder(ReadOnlySpan<byte> data)
    {
        var table = ImportFsst8(data);
        return FsstDecoder.FromSymbolTable(table);
    }

    /// <summary>Create an FSST12 decoder from exported bytes.</summary>
    public static Fsst12Decoder ImportFsst12Decoder(ReadOnlySpan<byte> data)
    {
        var map = ImportFsst12(data);
        return Fsst12Decoder.FromSymbolMap(map);
    }
}
