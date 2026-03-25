using System.Text;

namespace Fsst.Tests;

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
    public void Fsst8_ExportImport_CompressDecompressStillWorks()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("test data here ", 200)));
        var input = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(input);

        // Export and reimport
        var exported = FsstSerializer.ExportFsst8(table);
        var imported = FsstSerializer.ImportFsst8(exported);
        var decoder = FsstDecoder.FromSymbolTable(imported);

        // Compress with imported table, decompress
        var compressed = FsstEncoder.Compress(imported, data);
        var decompressed = decoder.Decompress(compressed, data.Length);
        Assert.Equal(data, decompressed);
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
        var decompressed = decoder.Decompress(compressed, data.Length);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_ImportInvalidData_Throws()
    {
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst8(new byte[5]));
    }

    [Fact]
    public void Fsst12_ImportInvalidData_Throws()
    {
        Assert.Throws<ArgumentException>(() => FsstSerializer.ImportFsst12(new byte[5]));
    }
}
