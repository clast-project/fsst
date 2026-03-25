using System.Text;
using BenchmarkDotNet.Attributes;

namespace Fsst.Benchmarks;

[MemoryDiagnoser]
public class DecompressionBenchmarks
{
    private byte[] _textData = null!;
    private byte[] _jsonData = null!;
    private byte[] _textCompressed8 = null!;
    private byte[] _jsonCompressed8 = null!;
    private byte[] _textCompressed12 = null!;
    private byte[] _jsonCompressed12 = null!;
    private FsstDecoder _textDecoder8 = null!;
    private FsstDecoder _jsonDecoder8 = null!;
    private Fsst12Decoder _textDecoder12 = null!;
    private Fsst12Decoder _jsonDecoder12 = null!;

    [Params(1024, 65536, 1048576)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);

        var textBuilder = new StringBuilder();
        var words = new[] { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "hello", "world" };
        while (textBuilder.Length < Size)
        {
            textBuilder.Append(words[rng.Next(words.Length)]);
            textBuilder.Append(' ');
        }
        _textData = Encoding.UTF8.GetBytes(textBuilder.ToString()[..Size]);

        var jsonBuilder = new StringBuilder();
        while (jsonBuilder.Length < Size)
        {
            jsonBuilder.Append($"{{\"id\":{rng.Next(10000)},\"name\":\"user{rng.Next(100)}\",\"active\":true}},");
        }
        _jsonData = Encoding.UTF8.GetBytes(jsonBuilder.ToString()[..Size]);

        // FSST8
        var textTable = FsstEncoder.BuildSymbolTable([_textData]);
        var jsonTable = FsstEncoder.BuildSymbolTable([_jsonData]);
        _textCompressed8 = FsstEncoder.Compress(textTable, _textData);
        _jsonCompressed8 = FsstEncoder.Compress(jsonTable, _jsonData);
        _textDecoder8 = FsstDecoder.FromSymbolTable(textTable);
        _jsonDecoder8 = FsstDecoder.FromSymbolTable(jsonTable);

        // FSST12
        var textMap = Fsst12Encoder.BuildSymbolTable([_textData]);
        var jsonMap = Fsst12Encoder.BuildSymbolTable([_jsonData]);
        _textCompressed12 = Fsst12Encoder.Compress(textMap, _textData);
        _jsonCompressed12 = Fsst12Encoder.Compress(jsonMap, _jsonData);
        _textDecoder12 = Fsst12Decoder.FromSymbolMap(textMap);
        _jsonDecoder12 = Fsst12Decoder.FromSymbolMap(jsonMap);
    }

    [Benchmark]
    public byte[] Fsst8_Text() => _textDecoder8.Decompress(_textCompressed8, _textData.Length);

    [Benchmark]
    public byte[] Fsst8_Json() => _jsonDecoder8.Decompress(_jsonCompressed8, _jsonData.Length);

    [Benchmark]
    public byte[] Fsst12_Text() => _textDecoder12.Decompress(_textCompressed12, _textData.Length);

    [Benchmark]
    public byte[] Fsst12_Json() => _jsonDecoder12.Decompress(_jsonCompressed12, _jsonData.Length);
}
