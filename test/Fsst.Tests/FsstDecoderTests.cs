using System.Text;

namespace Fsst.Tests;

public class FsstDecoderTests
{
    [Fact]
    public void Decompress_EmptyInput_ReturnsEmpty()
    {
        var decoder = new FsstDecoder();
        var result = decoder.Decompress([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Decompress_EscapedByte_ReturnsLiteral()
    {
        var decoder = new FsstDecoder();
        // Escape code 255 followed by literal 0x42
        byte[] compressed = [255, 0x42];
        var result = decoder.Decompress(compressed);
        Assert.Single(result);
        Assert.Equal(0x42, result[0]);
    }

    [Fact]
    public void Decompress_MultipleEscapedBytes()
    {
        var decoder = new FsstDecoder();
        byte[] compressed = [255, 0x41, 255, 0x42, 255, 0x43];
        var result = decoder.Decompress(compressed);
        Assert.Equal(3, result.Length);
        Assert.Equal("ABC"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_WithSymbol()
    {
        var decoder = new FsstDecoder();
        // Set up a symbol at code 0: "AB" (length 2)
        decoder.Len[0] = 2;
        decoder.DecoderSymbols[0] = (ulong)'A' | ((ulong)'B' << 8);

        byte[] compressed = [0]; // code 0
        var result = decoder.Decompress(compressed);
        Assert.Equal(2, result.Length);
        Assert.Equal((byte)'A', result[0]);
        Assert.Equal((byte)'B', result[1]);
    }
}
