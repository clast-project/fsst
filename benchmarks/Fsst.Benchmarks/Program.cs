using BenchmarkDotNet.Running;
using Fsst.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(CompressionBenchmarks).Assembly).Run(args);
