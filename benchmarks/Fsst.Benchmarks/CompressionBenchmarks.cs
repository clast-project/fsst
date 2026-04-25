// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using BenchmarkDotNet.Attributes;

namespace Clast.Fsst.Benchmarks;

[MemoryDiagnoser]
public class CompressionBenchmarks
{
    private byte[] _textData = null!;
    private byte[] _jsonData = null!;
    private byte[] _randomData = null!;
    private SymbolTable _textTable = null!;
    private SymbolTable _jsonTable = null!;
    private SymbolTable _randomTable = null!;
    private SymbolMap _textMap12 = null!;
    private SymbolMap _jsonMap12 = null!;

    [Params(1024, 65536, 1048576)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);

        // Text-like data
        var textBuilder = new StringBuilder();
        var words = new[] { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "hello", "world" };
        while (textBuilder.Length < Size)
        {
            textBuilder.Append(words[rng.Next(words.Length)]);
            textBuilder.Append(' ');
        }
        _textData = Encoding.UTF8.GetBytes(textBuilder.ToString()[..Size]);

        // JSON-like data
        var jsonBuilder = new StringBuilder();
        while (jsonBuilder.Length < Size)
        {
            jsonBuilder.Append($"{{\"id\":{rng.Next(10000)},\"name\":\"user{rng.Next(100)}\",\"active\":true}},");
        }
        _jsonData = Encoding.UTF8.GetBytes(jsonBuilder.ToString()[..Size]);

        // Random data
        _randomData = new byte[Size];
        rng.NextBytes(_randomData);

        // Build FSST8 tables
        _textTable = FsstEncoder.BuildSymbolTable([_textData]);
        _jsonTable = FsstEncoder.BuildSymbolTable([_jsonData]);
        _randomTable = FsstEncoder.BuildSymbolTable([_randomData]);

        // Build FSST12 tables
        _textMap12 = Fsst12Encoder.BuildSymbolTable([_textData]);
        _jsonMap12 = Fsst12Encoder.BuildSymbolTable([_jsonData]);
    }

    [Benchmark]
    public byte[] Fsst8_Text() => FsstEncoder.Compress(_textTable, _textData);

    [Benchmark]
    public byte[] Fsst8_Json() => FsstEncoder.Compress(_jsonTable, _jsonData);

    [Benchmark]
    public byte[] Fsst8_Random() => FsstEncoder.Compress(_randomTable, _randomData);

    [Benchmark]
    public byte[] Fsst12_Text() => Fsst12Encoder.Compress(_textMap12, _textData);

    [Benchmark]
    public byte[] Fsst12_Json() => Fsst12Encoder.Compress(_jsonMap12, _jsonData);
}
