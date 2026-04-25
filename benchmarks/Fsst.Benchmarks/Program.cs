// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Running;
using Clast.Fsst.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(CompressionBenchmarks).Assembly).Run(args);
