// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Clast.Fsst.Tests;

/// <summary>
/// Training walks hash structures whose iteration order decides which candidates win table slots.
/// That order must be a function of the input alone, or two runs over the same corpus produce
/// different symbol tables — which would be invisible in round-trip tests and only show up as
/// unreproducible files.
/// </summary>
public class TrainingDeterminismTests
{
    private static byte[][] Corpus()
    {
        var rnd = new Random(4242);
        string[] hosts = ["example.com", "contoso.org", "fabrikam.net"];
        var rows = new byte[3000][];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = Encoding.UTF8.GetBytes(
                $"https://{hosts[rnd.Next(hosts.Length)]}/items/{rnd.Next():x8}?ref={rnd.Next(200)}");
        return rows;
    }

    [Fact]
    public void Fsst8_TrainsIdenticallyAcrossRuns()
    {
        var rows = Corpus();
        var a = FsstEncoder.BuildSymbolTable(rows);
        var b = FsstEncoder.BuildSymbolTable(rows);

        Assert.Equal(a.NSymbols, b.NSymbols);
        Assert.Equal(a.LenHisto, b.LenHisto);
        Assert.Equal(FsstSerializer.ExportFsst8(a), FsstSerializer.ExportFsst8(b));
        Assert.Equal(
            FsstEncoder.CompressBatch(a, rows).compressedData,
            FsstEncoder.CompressBatch(b, rows).compressedData);
    }

    [Fact]
    public void Fsst12_TrainsIdenticallyAcrossRuns()
    {
        var rows = Corpus();
        var a = Fsst12Encoder.BuildSymbolTable(rows);
        var b = Fsst12Encoder.BuildSymbolTable(rows);

        Assert.Equal(a.NSymbols, b.NSymbols);
        Assert.Equal(
            Fsst12Encoder.CompressBatch(a, rows).compressedData,
            Fsst12Encoder.CompressBatch(b, rows).compressedData);
    }

    [Fact]
    public void Fsst16_TrainsIdenticallyAcrossRuns()
    {
        var rows = Corpus();
        var a = Fsst16Encoder.BuildSymbolTable(rows);
        var b = Fsst16Encoder.BuildSymbolTable(rows);

        Assert.Equal(a.SymbolCount, b.SymbolCount);

        var lengthsA = new byte[a.SymbolCount];
        var valuesA = new byte[a.SymbolCount * 16];
        var lengthsB = new byte[b.SymbolCount];
        var valuesB = new byte[b.SymbolCount * 16];
        a.ExportRaw(lengthsA, valuesA);
        b.ExportRaw(lengthsB, valuesB);

        Assert.Equal(lengthsA, lengthsB);
        Assert.Equal(valuesA, valuesB);
        Assert.Equal(
            Fsst16Encoder.CompressBatch(a, rows).compressedData,
            Fsst16Encoder.CompressBatch(b, rows).compressedData);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Fsst16_TrainsIdenticallyForEachMaxSymbolLength(int maxSymbolLength)
    {
        var rows = Corpus();
        var a = Fsst16Encoder.BuildSymbolTable(rows, maxSymbolLength);
        var b = Fsst16Encoder.BuildSymbolTable(rows, maxSymbolLength);

        Assert.Equal(
            Fsst16Encoder.CompressBatch(a, rows).compressedData,
            Fsst16Encoder.CompressBatch(b, rows).compressedData);
    }
}
