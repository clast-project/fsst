# Clast.Fsst

A C# implementation of **FSST** (Fast Static Symbol Table) string compression. FSST is a lightweight, byte-oriented dictionary compressor designed for columnar databases — it produces small, randomly-accessible compressed strings with very fast decompression.

This library is part of the `clast-project`.

## Features

- **FSST8** — 1-byte codes (up to 255 symbols) plus an escape byte for unmatched literals.
- **FSST12** — 12-bit codes packed two-per-three-bytes (up to 4096 symbols, no escape).
- **FSST16** — 2-byte codes (up to 65,535 symbols, symbols up to 16 bytes), the variant the Parquet FSST proposal calls `FSST_16`.
- Single-string and batch compress / decompress.
- Versioned binary format for serializing symbol tables.

## Target frameworks

| TFM              | Notes                                                                                 |
|------------------|---------------------------------------------------------------------------------------|
| `net10.0`        | Primary development target.                                                           |
| `net8.0`         | LTS.                                                                                  |
| `netstandard2.0` | Pulls in `System.Memory`; ships with internal polyfills for `BitOperations`, `Index`/`Range`, `KeyValuePair.Deconstruct`, etc. |

The test suite multi-targets `net48`, `net8.0`, and `net10.0` so the netstandard2.0 build is exercised end-to-end through a .NET Framework 4.8 host.

## Quick start

```csharp
using Clast.Fsst;
using System.Text;

var corpus = new[]
{
    Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"),
    Encoding.UTF8.GetBytes("the lazy dog sleeps in the shade"),
};

// Build a symbol table from a representative corpus.
SymbolTable table = FsstEncoder.BuildSymbolTable(corpus);

// Compress / decompress a single value.
byte[] compressed   = FsstEncoder.Compress(table, corpus[0]);
FsstDecoder decoder = FsstDecoder.FromSymbolTable(table);
byte[] roundtrip    = decoder.Decompress(compressed);

// Or compress / decompress a batch into caller-supplied buffers (Arrow-style prefix-sum offsets):
var (data, lengths) = FsstEncoder.CompressBatch(table, corpus);
var dst             = new byte[FsstDecoder.MaxDecompressedLength(data.Length)];
var offsets         = new int[corpus.Length + 1];
decoder.TryDecompressBatch(data, lengths, dst, offsets, out int totalWritten);
// Item i is now in dst[offsets[i]..offsets[i+1]].
```

### FSST12

`Fsst12Encoder` / `Fsst12Decoder` follow the same shape but use 12-bit codes (no escape, 1.5 bytes per code on average). Prefer FSST12 when the input has a large effective symbol vocabulary; prefer FSST8 when codes must be byte-aligned for cheap random-access decoding.

```csharp
SymbolMap map         = Fsst12Encoder.BuildSymbolTable(corpus);
byte[] compressed     = Fsst12Encoder.Compress(map, corpus[0]);
Fsst12Decoder decoder = Fsst12Decoder.FromSymbolMap(map);
byte[] roundtrip      = decoder.Decompress(compressed);
```

### FSST16

`Fsst16Encoder` / `Fsst16Decoder` use 16-bit codes written as little-endian `uint16`, with symbols
of up to 16 bytes. It buys a wider code space and longer symbols at 2 bytes per code, so it needs
symbols longer than 2 bytes to pay for itself, and training is slower.

Which variant wins is data-dependent, and FSST8 is a strong default: when 8-byte symbols cover the
data well it spends half the bits per code. On a 1.4 MB corpus of synthetic URLs, FSST8 reaches
3.11x against FSST16's 2.27x; on short name-like values the gap is wider still. FSST16 earns its
place where the effective symbol vocabulary is too large for 255 codes but the repeated substrings
are long — and it is the variant the Parquet proposal specifies, which is reason enough when writing
that format. Measure on your own data.

```csharp
SymbolTable16 table   = Fsst16Encoder.BuildSymbolTable(corpus);
byte[] compressed     = Fsst16Encoder.Compress(table, corpus[0]);
Fsst16Decoder decoder = Fsst16Decoder.FromSymbolTable(table);
byte[] roundtrip      = decoder.Decompress(compressed);
```

`BuildSymbolTable` takes an optional `maxSymbolLength` (default 16). The
[Parquet FSST proposal](https://github.com/apache/parquet-format/issues/531) contradicts itself on
the `FSST_16` cap — §1.2 says symbols are 1-8 bytes while §3.3, §3.5 and §3.6 all describe 16 — so
pass `8` to stay valid under the stricter reading:

```csharp
SymbolTable16 table = Fsst16Encoder.BuildSymbolTable(corpus, maxSymbolLength: 8);
```

Two properties are worth knowing when writing FSST16 into a container format:

- Tables from `BuildSymbolTable` always contain all 256 single-byte symbols. With 65,535 codes there
  is no reason to leave a byte to a 4-byte escape sequence, so these tables never escape and never
  expand beyond 2x. The escape code — 65,535, followed by the literal byte as a little-endian
  `uint16`, as the Parquet spec's §8.3 decode algorithm reads it — is still decoded, for tables
  produced elsewhere.
- Codes are assigned in ascending symbol-length order, so `ExportRaw` emits non-decreasing lengths
  and a consumer can derive a per-length histogram — and use the codes as-is — without rewriting
  every code in the compressed stream.

### Malformed input

All three decoders reject a corrupt code stream rather than decoding as much of it as they can. The
`Decompress(compressed, destination, out written)` overload returns a `System.Buffers.OperationStatus`
so a caller can tell corruption from a destination that is merely too small:

```csharp
switch (decoder.Decompress(compressed, dst, out int written))
{
    case OperationStatus.Done:                /* dst[..written] */      break;
    case OperationStatus.DestinationTooSmall: /* retry, larger */       break;
    case OperationStatus.InvalidData:         /* reject the value */    break;
}
```

`TryDecompress` returns false for either, and the allocating overloads (`Decompress(compressed)`,
`DecompressString`) throw `InvalidDataException` on corruption. `written` is 0 unless the status is
`Done` — but decoding writes as it goes, so the destination may already hold bytes decoded before
the problem was detected. Treat its contents as undefined unless the status is `Done`.

Rejected: a code at or beyond the symbol count, an escape marker with no literal after it, and — for
FSST16 — an odd-length stream or a literal above 255. These are the cases the Parquet FSST spec's
§8.3 decode algorithm calls errors, and §8.1 requires a reader to fail on. Validate one value at a
time: `TryDecompressBatch` / `DecompressBatch` slice per value first, so an escape at the end of one
value can never consume the next value's bytes.

### Persisting a symbol table

```csharp
byte[] tableBytes = FsstSerializer.ExportFsst8(table);
// ... store it, send it over the wire ...
SymbolTable restored = FsstSerializer.ImportFsst8(tableBytes);

// Or skip straight to a decoder:
FsstDecoder decoder = FsstSerializer.ImportFsst8Decoder(tableBytes);
```

The FSST8 export uses the cwida/fsst `fsst_export()` on-disk format (17-byte header
followed by raw symbol bytes in code order), so payloads are interoperable with the
reference C++ implementation.

`ExportFsst12` / `ImportFsst12` / `ImportFsst12Decoder` are the FSST12 equivalents
and use a separate length-prefixed framing (cwida does not publish an FSST12 export
format).

#### Bring-your-own framing

Other consumers (Lance, for example) wrap symbol tables in their own container.
`FsstDecoder.FromSymbols` skips the wire format entirely and takes pre-extracted
symbols indexed by code:

```csharp
// lengths[i] is the byte length of the symbol for code i (0 = unused slot).
// packedValues holds 8 little-endian bytes per code.
FsstDecoder decoder = FsstDecoder.FromSymbols(lengths, packedValues);
```

Parse your container's framing yourself, hand over the per-code lengths and
8-byte slots, and you get back a decoder.

`SymbolTable16.ExportRaw` / `Fsst16Decoder.FromSymbols` are the FSST16 pair. They use **16-byte**
slots rather than 8, since FSST16 symbols can be twice as long:

```csharp
var lengths = new byte[table.SymbolCount];
var values  = new byte[table.SymbolCount * 16];
table.ExportRaw(lengths, values);

Fsst16Decoder decoder = Fsst16Decoder.FromSymbols(lengths, values);
```

There is no `FsstSerializer` support for FSST16: cwida publishes no such format, and the Parquet
symbol-table page framing belongs in the consumer that writes Parquet.

## Project layout

```
src/Fsst/                   library (Clast.Fsst.dll)
test/Fsst.Tests/            xUnit test suite
benchmarks/Fsst.Benchmarks/ BenchmarkDotNet harness
```

## Build & test

```
dotnet build
dotnet test
```

## References

- **FSST: Fast Random Access String Compression** — Peter Boncz, Thomas Neumann, Viktor Leis. PVLDB Vol. 13, 2020.
- Reference C++ implementation: <https://github.com/cwida/fsst>

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
