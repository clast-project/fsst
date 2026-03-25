using System.Text;

namespace Fsst.Tests;

public class FsstRoundTripTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void Fsst8_RoundTrip_Identity(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_RepeatedPattern()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var data = Encoding.UTF8.GetBytes(repeated);
        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_AllByteValues()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;
        // Repeat to give the encoder something to work with
        var repeated = new byte[256 * 10];
        for (int i = 0; i < 10; i++) data.CopyTo(repeated.AsSpan(i * 256));

        var strings = new[] { repeated };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, repeated);
        var decompressed = decoder.Decompress(compressed, repeated.Length);

        Assert.Equal(repeated, decompressed);
    }

    [Fact]
    public void Fsst8_RoundTrip_BatchMultipleStrings()
    {
        var strings = new[]
        {
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("foo bar baz"),
            Encoding.UTF8.GetBytes("the quick brown fox"),
            Encoding.UTF8.GetBytes("aaaaaaaaaa"),
        };

        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var (compressedData, lengths) = FsstEncoder.CompressBatch(table, strings);
        var decompressed = decoder.DecompressBatch(compressedData, lengths);

        Assert.Equal(strings.Length, decompressed.Length);
        for (int i = 0; i < strings.Length; i++)
            Assert.Equal(strings[i], decompressed[i]);
    }

    [Fact]
    public void Fsst8_RoundTrip_EmptyStrings()
    {
        byte[][] strings = [[], []];
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var (compressedData, lengths) = FsstEncoder.CompressBatch(table, strings);
        var decompressed = decoder.DecompressBatch(compressedData, lengths);

        Assert.Equal(2, decompressed.Length);
        Assert.Empty(decompressed[0]);
        Assert.Empty(decompressed[1]);
    }

    [Fact]
    public void Fsst8_RoundTrip_LongString()
    {
        var rng = new Random(42);
        var data = new byte[100_000];
        // Mix of repeated patterns and random data
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 20 < 10)
                data[i] = (byte)('a' + (i % 10));
            else
                data[i] = (byte)rng.Next(256);
        }

        var strings = new[] { data };
        var table = FsstEncoder.BuildSymbolTable(strings);
        var decoder = FsstDecoder.FromSymbolTable(table);

        var compressed = FsstEncoder.Compress(table, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public void Fsst12_RoundTrip_Identity(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_RoundTrip_RepeatedPattern()
    {
        var repeated = string.Concat(Enumerable.Repeat("abcdefgh", 200));
        var data = Encoding.UTF8.GetBytes(repeated);
        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_RoundTrip_AllByteValues()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;

        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Fsst12_RoundTrip_LongString()
    {
        var rng = new Random(42);
        var data = new byte[50_000];
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 20 < 10)
                data[i] = (byte)('a' + (i % 10));
            else
                data[i] = (byte)rng.Next(256);
        }

        var strings = new[] { data };
        var map = Fsst12Encoder.BuildSymbolTable(strings);
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        var compressed = Fsst12Encoder.Compress(map, data);
        var decompressed = decoder.Decompress(compressed, data.Length);

        Assert.Equal(data, decompressed);
    }
}
