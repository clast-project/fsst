# Clast.Fsst

A C# implementation of **FSST** (Fast Static Symbol Table) string compression. FSST is a lightweight, byte-oriented dictionary compressor designed for columnar databases — it produces small, randomly-accessible compressed strings with very fast decompression.

This library is part of the `clast-project`.

## Features

- **FSST8** — 1-byte codes (up to 255 symbols) plus an escape byte for unmatched literals.
- **FSST12** — 12-bit codes packed two-per-three-bytes (up to 4096 symbols, no escape).
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
byte[] roundtrip    = decoder.Decompress(compressed, originalLength: corpus[0].Length);

// Or compress a batch:
var (data, lengths) = FsstEncoder.CompressBatch(table, corpus);
byte[][] decoded    = decoder.DecompressBatch(data, lengths);
```

### FSST12

`Fsst12Encoder` / `Fsst12Decoder` follow the same shape but use 12-bit codes (no escape, 1.5 bytes per code on average). Prefer FSST12 when the input has a large effective symbol vocabulary; prefer FSST8 when codes must be byte-aligned for cheap random-access decoding.

```csharp
SymbolMap map         = Fsst12Encoder.BuildSymbolTable(corpus);
byte[] compressed     = Fsst12Encoder.Compress(map, corpus[0]);
Fsst12Decoder decoder = Fsst12Decoder.FromSymbolMap(map);
byte[] roundtrip      = decoder.Decompress(compressed, originalLength: corpus[0].Length);
```

### Persisting a symbol table

```csharp
byte[] tableBytes = FsstSerializer.ExportFsst8(table);
// ... store it, send it over the wire ...
SymbolTable restored = FsstSerializer.ImportFsst8(tableBytes);

// Or skip straight to a decoder:
FsstDecoder decoder = FsstSerializer.ImportFsst8Decoder(tableBytes);
```

`ExportFsst12` / `ImportFsst12` / `ImportFsst12Decoder` are the FSST12 equivalents.

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
