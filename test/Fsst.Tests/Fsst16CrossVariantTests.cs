// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Clast.Fsst.Tests;

/// <summary>
/// Differential tests that hold the FSST16 path against the FSST8 path on an identical symbol set.
///
/// Nothing in the ecosystem reads or writes FSST_16 today, so the compressed stream itself cannot be
/// validated against a peer implementation. What can be validated is the part that does have a
/// second implementation here: given the same symbols and the same input, the greedy longest-match
/// parse must be identical. The two paths share no code — FSST8 uses <see cref="Symbol"/> (64-bit
/// value, 1024-slot hash, 9-bit codes) and FSST16 uses <see cref="Symbol16"/> (128-bit value,
/// 131072-slot hash, 16-bit codes) — so a bug in Symbol16's masking, Icl ordering, hashing or
/// prefix lookups shows up as a divergent parse.
///
/// The symbol sets travel through cwida's <c>fsst_export</c> framing on the way in, so a real
/// cwida-produced payload can be dropped into <see cref="LiftCwidaPayload"/> unchanged when one
/// becomes available.
///
/// Coverage note: trained FSST8 tables in practice hold no length-2 symbols (the trainer's
/// candidate doubling goes 1 -> 2 -> 4 and 2-byte candidates lose on gain), so the corpus-driven
/// comparisons exercise lengths 1 and 3-8 plus escapes. The 2-byte prefix path is covered by
/// <see cref="SameSymbolSet_ProducesIdenticalParse_ForLength2Symbols"/>, which builds that case
/// explicitly from a cwida payload.
/// </summary>
public class Fsst16CrossVariantTests
{
    /// <summary>
    /// Rebuild an FSST8 symbol table's symbols as an FSST16 table. Codes correspond 1:1, since both
    /// <c>Add</c> implementations assign codes sequentially in insertion order.
    /// </summary>
    private static SymbolTable16 Lift(SymbolTable source)
    {
        var lifted = new SymbolTable16(Symbol.MaxLength);

        for (int code = 0; code < source.NSymbols; code++)
        {
            var sym = source.Symbols[code];
            Span<byte> bytes = stackalloc byte[Symbol.MaxLength];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, sym.Val);

            // A symbol that fit FSST8's 1024-slot hash cannot collide in a 131072-slot table:
            // distinct low 10 bits imply distinct low 17 bits. So every Add must succeed.
            Assert.True(
                lifted.Add(Symbol16.FromSpan(bytes.Slice(0, sym.Length()))),
                $"FSST16 rejected symbol at code {code} (length {sym.Length()}).");
        }

        Assert.Equal(source.NSymbols, lifted.SymbolCount);
        return lifted;
    }

    /// <summary>Parse an FSST8 stream into its code sequence, mapping the escape code onto FSST16's.</summary>
    private static List<int> Fsst8Codes(byte[] compressed)
    {
        var codes = new List<int>();
        for (int i = 0; i < compressed.Length;)
        {
            byte code = compressed[i++];
            if (code == 255)
            {
                codes.Add(SymbolTable16.EscCode);
                i++; // literal byte
            }
            else
            {
                codes.Add(code);
            }
        }
        return codes;
    }

    /// <summary>Parse an FSST16 stream into its code sequence.</summary>
    private static List<int> Fsst16Codes(byte[] compressed)
    {
        var codes = new List<int>();
        for (int i = 0; i + 2 <= compressed.Length;)
        {
            int code = compressed[i] | (compressed[i + 1] << 8);
            i += 2;
            codes.Add(code);
            if (code == SymbolTable16.EscCode) i++; // literal byte
        }
        return codes;
    }

    /// <summary>Text the trainer can work with, followed by every byte value so escapes are exercised.</summary>
    private static byte[] MixedCorpus()
    {
        var text = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 60)));
        var result = new byte[text.Length + 256];
        text.CopyTo(result, 0);
        for (int i = 0; i < 256; i++) result[text.Length + i] = (byte)i;
        return result;
    }

    [Fact]
    public void SameSymbolSet_ProducesIdenticalParse()
    {
        var data = MixedCorpus();

        // Train with FSST8, then round-trip the table through cwida's fsst_export framing.
        var trained = FsstEncoder.BuildSymbolTable([data]);
        var table8 = FsstSerializer.ImportFsst8(FsstSerializer.ExportFsst8(trained));
        var table16 = Lift(table8);

        var compressed8 = FsstEncoder.Compress(table8, data);
        var compressed16 = Fsst16Encoder.Compress(table16, data);

        // The strong assertion: both matchers pick the same symbol at every position.
        Assert.Equal(Fsst8Codes(compressed8), Fsst16Codes(compressed16));

        // And both round-trip.
        Assert.Equal(data, FsstDecoder.FromSymbolTable(table8).Decompress(compressed8));
        Assert.Equal(data, Fsst16Decoder.FromSymbolTable(table16).Decompress(compressed16));
    }

    [Fact]
    public void SameSymbolSet_ParseIncludesEscapes()
    {
        // Guards the test above: if the corpus stopped producing escapes, the comparison would
        // silently cover less than it claims.
        var data = MixedCorpus();
        var table8 = FsstEncoder.BuildSymbolTable([data]);
        var table16 = Lift(table8);

        var codes = Fsst16Codes(Fsst16Encoder.Compress(table16, data));
        Assert.Contains(SymbolTable16.EscCode, codes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello world")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void SameSymbolSet_ProducesIdenticalParse_ForUnseenInput(string input)
    {
        // Train on one corpus, compress a different one: exercises the fallback chain
        // (hash miss -> 2-byte -> 1-byte -> escape) rather than the trained-for path.
        var trained = FsstEncoder.BuildSymbolTable([MixedCorpus()]);
        var table8 = FsstSerializer.ImportFsst8(FsstSerializer.ExportFsst8(trained));
        var table16 = Lift(table8);

        var data = Encoding.UTF8.GetBytes(input);
        Assert.Equal(
            Fsst8Codes(FsstEncoder.Compress(table8, data)),
            Fsst16Codes(Fsst16Encoder.Compress(table16, data)));
    }

    /// <summary>
    /// A cwida <c>fsst_export</c> payload holding two length-2 symbols, "ab" (code 0) and
    /// "cd" (code 1). Symbol content is defined by cwida's documented layout rather than by our
    /// own encoder, so this is the slot a real cwida-produced payload drops into.
    /// </summary>
    private static byte[] LiftCwidaPayload()
    {
        var payload = new byte[17 + 4];
        ulong version = (20190218UL << 32) | (2UL << 24) | (0UL << 16) | (2UL << 8) | 1UL;
        BinaryPrimitives.WriteUInt64LittleEndian(payload, version);
        payload[8] = 0;      // not zeroTerminated
        payload[9 + 1] = 2;  // lenHisto[1]: two length-2 symbols
        payload[17] = (byte)'a'; payload[18] = (byte)'b';
        payload[19] = (byte)'c'; payload[20] = (byte)'d';
        return payload;
    }

    [Fact]
    public void CwidaPayload_Length2Symbols_CompressToTheExpectedCodes()
    {
        var table16 = Lift(FsstSerializer.ImportFsst8(LiftCwidaPayload()));

        var data = Encoding.UTF8.GetBytes("abcd!abZcd");
        var compressed16 = Fsst16Encoder.Compress(table16, data);

        // Codes 0, 1, escape '!', 0, escape 'Z', 1 — dictated by the symbol set, not by our encoder.
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x01, 0x00, 0xFF, 0xFF, (byte)'!', 0x00, 0x00, 0xFF, 0xFF, (byte)'Z', 0x01, 0x00 },
            compressed16);

        Assert.Equal(data, Fsst16Decoder.FromSymbolTable(table16).Decompress(compressed16));
    }

    [Fact]
    public void SameSymbolSet_ProducesIdenticalParse_ForLength2Symbols()
    {
        var table8 = FsstSerializer.ImportFsst8(LiftCwidaPayload());
        var table16 = Lift(table8);

        var data = Encoding.UTF8.GetBytes("abcd!abZcd");

        Assert.Equal(
            Fsst8Codes(FsstEncoder.Compress(table8, data)),
            Fsst16Codes(Fsst16Encoder.Compress(table16, data)));
    }

    /// <summary>Deliberately naive FSST16 decoder: no fast paths, no unsafe writes.</summary>
    private static byte[] NaiveDecode(byte[][] symbolsByCode, byte[] compressed)
    {
        var output = new List<byte>();
        for (int i = 0; i + 2 <= compressed.Length;)
        {
            int code = compressed[i] | (compressed[i + 1] << 8);
            i += 2;

            if (code == SymbolTable16.EscCode)
            {
                if (i >= compressed.Length) break; // dangling escape
                output.Add(compressed[i++]);
            }
            else if (code < symbolsByCode.Length)
            {
                output.AddRange(symbolsByCode[code]);
            }
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Decoder_MatchesNaiveReferenceDecoder(int maxSymbolLength)
    {
        // Covers symbol lengths 9-16, which no FSST8 table can supply, by holding the optimized
        // decoder against a reference that just appends bytes.
        var data = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("https://www.example.com/catalog/items/0123456789 ", 200)));

        var table = Fsst16Encoder.BuildSymbolTable([data], maxSymbolLength);
        var compressed = Fsst16Encoder.Compress(table, data);

        var lengths = new byte[table.SymbolCount];
        var values = new byte[table.SymbolCount * 16];
        table.ExportRaw(lengths, values);

        var symbolsByCode = new byte[table.SymbolCount][];
        for (int code = 0; code < table.SymbolCount; code++)
        {
            symbolsByCode[code] = new byte[lengths[code]];
            Buffer.BlockCopy(values, code * 16, symbolsByCode[code], 0, lengths[code]);
        }

        var expected = NaiveDecode(symbolsByCode, compressed);
        Assert.Equal(data, expected);

        var decoder = Fsst16Decoder.FromSymbolTable(table);

        // Generous destination: takes the 16-byte-at-a-time write path.
        Assert.Equal(expected, decoder.Decompress(compressed));

        // Exactly-sized destination: forces the careful tail path for the trailing symbols.
        var exact = new byte[expected.Length];
        Assert.True(decoder.TryDecompress(compressed, exact, out int written));
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, exact);
    }
}
