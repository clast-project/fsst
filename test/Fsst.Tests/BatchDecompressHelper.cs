// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Fsst.Tests;

/// <summary>
/// Test-only convenience that mirrors the old byte[][]-returning DecompressBatch API
/// on top of the span-based TryDecompressBatch the product now exposes. Keeps the
/// existing round-trip assertions concise without adding allocation-heavy overloads
/// to the shipping decoders.
/// </summary>
internal static class BatchDecompressHelper
{
    public static byte[][] DecompressBatch(this FsstDecoder decoder, ReadOnlySpan<byte> compressedData, ReadOnlySpan<int> lengths)
    {
        var dst = new byte[FsstDecoder.MaxDecompressedLength(compressedData.Length)];
        var offsets = new int[lengths.Length + 1];
        if (!decoder.TryDecompressBatch(compressedData, lengths, dst, offsets, out _))
            throw new InvalidOperationException("TryDecompressBatch failed; destination sized via MaxDecompressedLength.");
        return Slice(dst, offsets);
    }

    public static byte[][] DecompressBatch(this Fsst12Decoder decoder, ReadOnlySpan<byte> compressedData, ReadOnlySpan<int> lengths)
    {
        var dst = new byte[Fsst12Decoder.MaxDecompressedLength(compressedData.Length)];
        var offsets = new int[lengths.Length + 1];
        if (!decoder.TryDecompressBatch(compressedData, lengths, dst, offsets, out _))
            throw new InvalidOperationException("TryDecompressBatch failed; destination sized via MaxDecompressedLength.");
        return Slice(dst, offsets);
    }

    private static byte[][] Slice(byte[] dst, int[] offsets)
    {
        var result = new byte[offsets.Length - 1][];
        for (int i = 0; i < result.Length; i++)
        {
            int len = offsets[i + 1] - offsets[i];
            var item = new byte[len];
            Buffer.BlockCopy(dst, offsets[i], item, 0, len);
            result[i] = item;
        }
        return result;
    }
}
