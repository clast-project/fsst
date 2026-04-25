// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;

namespace Clast.Fsst.Tests;

public class BufferOverloadTests
{
    private static SymbolTable Fsst8Table()
    {
        var corpus = new[] { Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 200))) };
        return FsstEncoder.BuildSymbolTable(corpus);
    }

    private static SymbolMap Fsst12Map()
    {
        var corpus = new[] { Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 200))) };
        return Fsst12Encoder.BuildSymbolTable(corpus);
    }

    [Fact]
    public void Fsst8_MaxCompressedLength_Bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FsstEncoder.MaxCompressedLength(-1));
        Assert.Equal(0, FsstEncoder.MaxCompressedLength(0));
        Assert.Equal(2000, FsstEncoder.MaxCompressedLength(1000));
    }

    [Fact]
    public void Fsst8_TryCompress_EmptyInput_Succeeds()
    {
        var table = Fsst8Table();
        Assert.True(FsstEncoder.TryCompress(table, [], [], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst8_TryCompress_ExactDestination_RoundTrips()
    {
        var table = Fsst8Table();
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));

        // First, find the actual compressed length.
        var reference = FsstEncoder.Compress(table, input);
        var dest = new byte[reference.Length];

        Assert.True(FsstEncoder.TryCompress(table, input, dest, out int written));
        Assert.Equal(reference.Length, written);
        Assert.Equal(reference, dest);

        var decoder = FsstDecoder.FromSymbolTable(table);
        Assert.Equal(input, decoder.Decompress(dest));
    }

    [Fact]
    public void Fsst8_TryCompress_DestinationTooSmall_ReturnsFalse()
    {
        var table = Fsst8Table();
        var input = Encoding.UTF8.GetBytes("the quick brown fox");

        Assert.False(FsstEncoder.TryCompress(table, input, new byte[1], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst8_Compress_IBufferWriter_RoundTrips()
    {
        var table = Fsst8Table();
        var input = Encoding.UTF8.GetBytes("hello hello hello world");
        var writer = new ListBufferWriter();

        FsstEncoder.Compress(table, input, writer);

        var decoder = FsstDecoder.FromSymbolTable(table);
        Assert.Equal(input, decoder.Decompress(writer.WrittenSpan.ToArray()));
    }

    [Fact]
    public void Fsst8_Compress_IBufferWriter_AppendsAcrossMultipleCalls()
    {
        var table = Fsst8Table();
        var inputs = new[]
        {
            Encoding.UTF8.GetBytes("hello world"),
            Encoding.UTF8.GetBytes("foo bar baz"),
        };
        var writer = new ListBufferWriter();
        var lengths = new int[inputs.Length];

        for (int i = 0; i < inputs.Length; i++)
        {
            int before = writer.WrittenCount;
            FsstEncoder.Compress(table, inputs[i], writer);
            lengths[i] = writer.WrittenCount - before;
        }

        var decoder = FsstDecoder.FromSymbolTable(table);
        var roundtrip = decoder.DecompressBatch(writer.WrittenSpan.ToArray(), lengths);
        Assert.Equal(inputs.Length, roundtrip.Length);
        for (int i = 0; i < inputs.Length; i++)
            Assert.Equal(inputs[i], roundtrip[i]);
    }

    [Fact]
    public void Fsst8_Compress_IBufferWriter_NullWriter_Throws()
    {
        var table = Fsst8Table();
        Assert.Throws<ArgumentNullException>(() => FsstEncoder.Compress(table, [(byte)'x'], null!));
    }

    [Fact]
    public void Fsst12_MaxCompressedLength_Bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst12Encoder.MaxCompressedLength(-1));
        Assert.Equal(0, Fsst12Encoder.MaxCompressedLength(0));
        // 1000 input bytes → at most 1000 codes → ceil(1000 * 1.5) = 1500
        Assert.Equal(1500, Fsst12Encoder.MaxCompressedLength(1000));
    }

    [Fact]
    public void Fsst12_TryCompress_EmptyInput_Succeeds()
    {
        var map = Fsst12Map();
        Assert.True(Fsst12Encoder.TryCompress(map, [], [], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst12_TryCompress_ExactDestination_RoundTrips()
    {
        var map = Fsst12Map();
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));

        var reference = Fsst12Encoder.Compress(map, input);
        var dest = new byte[reference.Length];

        Assert.True(Fsst12Encoder.TryCompress(map, input, dest, out int written));
        Assert.Equal(reference.Length, written);
        Assert.Equal(reference, dest);

        var decoder = Fsst12Decoder.FromSymbolMap(map);
        Assert.Equal(input, decoder.Decompress(dest));
    }

    [Fact]
    public void Fsst12_TryCompress_DestinationTooSmall_ReturnsFalse()
    {
        var map = Fsst12Map();
        var input = Encoding.UTF8.GetBytes("the quick brown fox jumps over");

        Assert.False(Fsst12Encoder.TryCompress(map, input, new byte[1], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst12_Compress_IBufferWriter_RoundTrips()
    {
        var map = Fsst12Map();
        var input = Encoding.UTF8.GetBytes("hello hello hello world");
        var writer = new ListBufferWriter();

        Fsst12Encoder.Compress(map, input, writer);

        var decoder = Fsst12Decoder.FromSymbolMap(map);
        Assert.Equal(input, decoder.Decompress(writer.WrittenSpan.ToArray()));
    }

    [Fact]
    public void Fsst12_Compress_IBufferWriter_NullWriter_Throws()
    {
        var map = Fsst12Map();
        Assert.Throws<ArgumentNullException>(() => Fsst12Encoder.Compress(map, [(byte)'x'], null!));
    }

    // Decoder side

    [Fact]
    public void Fsst8_MaxDecompressedLength_Bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FsstDecoder.MaxDecompressedLength(-1));
        Assert.Equal(0, FsstDecoder.MaxDecompressedLength(0));
        Assert.Equal(800, FsstDecoder.MaxDecompressedLength(100));
    }

    [Fact]
    public void Fsst8_TryDecompress_EmptyInput_Succeeds()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);
        Assert.True(decoder.TryDecompress([], [], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst8_TryDecompress_ExactDestination_RoundTrips()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));
        var compressed = FsstEncoder.Compress(table, input);

        var dest = new byte[input.Length];
        Assert.True(decoder.TryDecompress(compressed, dest, out int written));
        Assert.Equal(input.Length, written);
        Assert.Equal(input, dest);
    }

    [Fact]
    public void Fsst8_TryDecompress_DestinationTooSmall_ReturnsFalse()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));
        var compressed = FsstEncoder.Compress(table, input);

        Assert.False(decoder.TryDecompress(compressed, new byte[1], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst8_Decompress_IBufferWriter_RoundTrips()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);
        var input = Encoding.UTF8.GetBytes("hello hello hello world");
        var compressed = FsstEncoder.Compress(table, input);

        var writer = new ListBufferWriter();
        decoder.Decompress(compressed, writer);

        Assert.Equal(input, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Fsst8_Decompress_IBufferWriter_NullWriter_Throws()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);
        Assert.Throws<ArgumentNullException>(() => decoder.Decompress(new byte[] { 255, 0x42 }, null!));
    }

    [Fact]
    public void Fsst12_MaxDecompressedLength_Bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fsst12Decoder.MaxDecompressedLength(-1));
        Assert.Equal(0, Fsst12Decoder.MaxDecompressedLength(0));
        // 3 compressed bytes -> 2 codes -> up to 16 output bytes.
        Assert.Equal(16, Fsst12Decoder.MaxDecompressedLength(3));
        Assert.Equal(32, Fsst12Decoder.MaxDecompressedLength(6));
    }

    [Fact]
    public void Fsst12_TryDecompress_EmptyInput_Succeeds()
    {
        var map = Fsst12Map();
        var decoder = Fsst12Decoder.FromSymbolMap(map);
        Assert.True(decoder.TryDecompress([], [], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst12_TryDecompress_ExactDestination_RoundTrips()
    {
        var map = Fsst12Map();
        var decoder = Fsst12Decoder.FromSymbolMap(map);
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));
        var compressed = Fsst12Encoder.Compress(map, input);

        var dest = new byte[input.Length];
        Assert.True(decoder.TryDecompress(compressed, dest, out int written));
        Assert.Equal(input.Length, written);
        Assert.Equal(input, dest);
    }

    [Fact]
    public void Fsst12_TryDecompress_DestinationTooSmall_ReturnsFalse()
    {
        var map = Fsst12Map();
        var decoder = Fsst12Decoder.FromSymbolMap(map);
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 50)));
        var compressed = Fsst12Encoder.Compress(map, input);

        Assert.False(decoder.TryDecompress(compressed, new byte[1], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Fsst12_Decompress_IBufferWriter_RoundTrips()
    {
        var map = Fsst12Map();
        var decoder = Fsst12Decoder.FromSymbolMap(map);
        var input = Encoding.UTF8.GetBytes("hello hello hello world");
        var compressed = Fsst12Encoder.Compress(map, input);

        var writer = new ListBufferWriter();
        decoder.Decompress(compressed, writer);

        Assert.Equal(input, writer.WrittenSpan.ToArray());
    }

    // String overloads

    [Fact]
    public void Fsst8_String_RoundTrips()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);

        const string input = "the quick brown fox jumps over the lazy dog";
        var compressed = FsstEncoder.Compress(table, input);
        var decompressed = decoder.DecompressString(compressed);

        Assert.Equal(input, decompressed);
    }

    [Fact]
    public void Fsst8_String_EmptyRoundTrips()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);

        Assert.Empty(FsstEncoder.Compress(table, string.Empty));
        Assert.Equal(string.Empty, decoder.DecompressString([]));
    }

    [Fact]
    public void Fsst8_String_HandlesUnicode()
    {
        var table = Fsst8Table();
        var decoder = FsstDecoder.FromSymbolTable(table);

        const string input = "héllo wörld — 日本語";
        var compressed = FsstEncoder.Compress(table, input);
        Assert.Equal(input, decoder.DecompressString(compressed));
    }

    [Fact]
    public void Fsst12_String_RoundTrips()
    {
        var map = Fsst12Map();
        var decoder = Fsst12Decoder.FromSymbolMap(map);

        const string input = "the quick brown fox jumps over the lazy dog";
        var compressed = Fsst12Encoder.Compress(map, input);
        var decompressed = decoder.DecompressString(compressed);

        Assert.Equal(input, decompressed);
    }
}

/// <summary>
/// Minimal IBufferWriter&lt;byte&gt; for tests. Avoids depending on ArrayBufferWriter,
/// which isn't in System.Memory 4.5.x for netstandard2.0.
/// </summary>
internal sealed class ListBufferWriter : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[256];
    private int _written;

    public int WrittenCount => _written;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= 0) sizeHint = 1;
        int needed = _written + sizeHint;
        if (needed <= _buffer.Length) return;
        int newSize = Math.Max(_buffer.Length * 2, needed);
        Array.Resize(ref _buffer, newSize);
    }
}
