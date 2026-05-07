using BenchmarkDotNet.Running;
using Sieve.Benchmarks;

// BenchmarkDotNet REQUIRES Release mode.
// Running in Debug will produce a warning and exit.
BenchmarkRunner.Run<ParserBenchmarks>();
