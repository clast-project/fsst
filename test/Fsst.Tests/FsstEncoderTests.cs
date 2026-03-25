using System.Text;

namespace Fsst.Tests;

public class FsstEncoderTests
{
    [Fact]
    public void BuildSymbolTable_EmptyInput_ReturnsEmptyTable()
    {
        var table = FsstEncoder.BuildSymbolTable([]);
        Assert.Equal(0, table.NSymbols);
    }

    [Fact]
    public void BuildSymbolTable_SingleString_ProducesSymbols()
    {
        var input = new[] { Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog") };
        var table = FsstEncoder.BuildSymbolTable(input);
        Assert.True(table.NSymbols > 0);
    }

    [Fact]
    public void BuildSymbolTable_RepeatedPatterns_ProducesMoreSymbols()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdef", 100));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var table = FsstEncoder.BuildSymbolTable(input);
        Assert.True(table.NSymbols > 0);
    }

    [Fact]
    public void Compress_EmptyInput_ReturnsEmpty()
    {
        var table = new SymbolTable();
        var result = FsstEncoder.Compress(table, []);
        Assert.Empty(result);
    }

    [Fact]
    public void Compress_SingleByte_ProducesEscapedOutput()
    {
        var table = new SymbolTable();
        table.Finalize(false);
        var result = FsstEncoder.Compress(table, [0x42]);
        // With no real symbols, should escape: [255, 0x42]
        Assert.Equal(2, result.Length);
        Assert.Equal(255, result[0]);
        Assert.Equal(0x42, result[1]);
    }

    [Fact]
    public void Compress_WithSymbols_ProducesSmallerOutput()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var input = new[] { Encoding.UTF8.GetBytes(repeated) };
        var table = FsstEncoder.BuildSymbolTable(input);
        var data = Encoding.UTF8.GetBytes(repeated);
        var compressed = FsstEncoder.Compress(table, data);

        // Compressed should be smaller than original
        Assert.True(compressed.Length < data.Length,
            $"Compressed {compressed.Length} should be < original {data.Length}");
    }

    [Fact]
    public void CompressBatch_MultipleStrings()
    {
        var strings = new[]
        {
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("hello there"),
            Encoding.UTF8.GetBytes("world hello"),
        };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var (data, lengths) = FsstEncoder.CompressBatch(table, strings);

        Assert.Equal(3, lengths.Length);
        Assert.Equal(data.Length, lengths.Sum());
    }
}
