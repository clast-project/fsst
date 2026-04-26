// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Fsst;

/// <summary>
/// Export/import symbol tables for both FSST8 and FSST12 variants.
/// </summary>
public static class FsstSerializer
{
    // FSST8 uses the cwida/fsst on-disk format produced by fsst_export().
    // Header layout (17 bytes), little-endian:
    //   bytes 0..7  : packed version word
    //                   bits 32..63 : version magic (Fsst8Version)
    //                   bits 24..31 : suffixLim
    //                   bits 16..23 : terminator
    //                   bits  8..15 : nSymbols
    //                   bits  0.. 7 : endian marker (always 1 on little-endian writers)
    //   byte  8     : zeroTerminated flag (low bit)
    //   bytes 9..16 : length histogram, 1 byte per entry; index 0 holds the count of
    //                 length-1 symbols, 1 holds length-2, ..., 7 holds length-8.
    // Symbol bytes follow in code order; the implicit zero terminator (when zeroTerminated is set)
    // is omitted. Matches https://github.com/cwida/fsst libfsst.cpp::fsst_export verbatim.
    private const ulong Fsst8Version = 20190218UL;
    private const byte Fsst8EndianMarker = 1;
    private const int Fsst8HeaderLength = 17;

    private const ulong Fsst12Version = 20190219UL;

    /// <summary>
    /// Export an FSST8 symbol table in the cwida/fsst <c>fsst_export()</c> format,
    /// interoperable with the reference C++ implementation and Lance.
    /// </summary>
    public static byte[] ExportFsst8(SymbolTable table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        int zt = table.ZeroTerminated ? 1 : 0;
        int totalLen = Fsst8HeaderLength;
        for (int i = zt; i < table.NSymbols; i++)
            totalLen += table.Symbols[i].Length();

        var buf = new byte[totalLen];

        ulong version = (Fsst8Version << 32)
                      | ((ulong)(byte)table.SuffixLim << 24)
                      | ((ulong)(byte)table.Terminator << 16)
                      | ((ulong)(byte)table.NSymbols << 8)
                      | Fsst8EndianMarker;
        BinaryPrimitives.WriteUInt64LittleEndian(buf, version);

        buf[8] = (byte)zt;
        for (int i = 0; i < 8; i++)
            buf[9 + i] = (byte)table.LenHisto[i];

        int pos = Fsst8HeaderLength;
        for (int i = zt; i < table.NSymbols; i++)
        {
            var sym = table.Symbols[i];
            int len = sym.Length();
            ulong v = sym.Val;
            for (int j = 0; j < len; j++)
                buf[pos++] = (byte)(v >> (j * 8));
        }
        return buf;
    }

    /// <summary>
    /// Import an FSST8 symbol table from a cwida/fsst <c>fsst_export()</c> payload.
    /// </summary>
    public static SymbolTable ImportFsst8(ReadOnlySpan<byte> data)
    {
        if (data.Length < Fsst8HeaderLength)
            throw new ArgumentException("FSST8 payload is shorter than the 17-byte header.", nameof(data));

        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(data);
        ulong fsstVersion = version >> 32;
        if (fsstVersion != Fsst8Version)
            throw new ArgumentException($"Unknown FSST8 version: {fsstVersion}", nameof(data));
        byte endianMarker = (byte)(version & 0xFF);
        if (endianMarker != Fsst8EndianMarker)
            throw new ArgumentException("FSST8 payload has an unsupported endian marker.", nameof(data));

        int nSymbols = (int)((version >> 8) & 0xFF);
        int terminator = (int)((version >> 16) & 0xFF);
        int suffixLim = (int)((version >> 24) & 0xFF);
        bool zeroTerminated = (data[8] & 1) != 0;

        var table = new SymbolTable();
        table.LoadCwidaPayload(suffixLim, terminator, zeroTerminated, data.Slice(9, 8), nSymbols, data[Fsst8HeaderLength..]);
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
