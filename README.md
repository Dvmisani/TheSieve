# The Sieve

C# log processing pipeline designed to survive "Log Storms" scenarios where a single error fires thousands of times per second and would ordinarily crush downstream systems.

The core goal was zero heap allocations on the hot path. Every design decision traces back to that constraint.

---

## The problem it solves

When a database connection pool exhausts, you don't get one error — you get 5,000 identical errors per second. Standard logging pipelines (Regex, `string.Split`, `StreamReader.ReadLine`) allocate a new string for every line they touch. At that volume, you're running the GC constantly, which pauses your threads, which makes the outage worse.

The Sieve intercepts at the source: parse without allocating, deduplicate in memory, let only unique errors through.

---

## Architecture

```
TheSieve/
├── src/
│   ├── Sieve.Core/
│   │   ├── Parser/          — readonly ref struct parser, zero-alloc
│   │   ├── Deduplication/   — sliding window deduplicator
│   │   └── Pipeline/        — System.IO.Pipelines hot path
│   ├── Sieve.Chaos/         — multi-threaded log storm generator
│   └── Sieve.Benchmarks/    — BenchmarkDotNet comparisons
└── tests/
    └── Sieve.Core.Tests/    — xUnit + FluentAssertions
```

---

## Key technical decisions

**`readonly ref struct` for LogEntry**

A `ref struct` cannot be boxed or stored on the heap. The CLR enforces this at compile time. The result is that every `LogEntry` we create lives and dies within a single stack frame — the GC never sees it. At 1 million log lines, that's 1 million objects that never need collecting.

**`ReadOnlySpan<char>` instead of substrings**

Every field in `LogEntry` (timestamp, service name, message) is a `ReadOnlySpan<char>` — a fat pointer into the original string's memory. No substrings are created during parsing. When you call `entry.ServiceName.ToString()`, *that's* when an allocation happens, and that's intentional — it's explicit in the API.

**`SearchValues<char>` for bracket scanning**

.NET 8 introduced `SearchValues<T>`, which builds a lookup bitmap at startup and uses SIMD instructions (`IndexOfAny`) to scan for characters. On AVX2 hardware this processes 32 characters per CPU cycle versus 1 for a naive loop. The bracket scanner that runs on every log line uses this.

**`System.IO.Pipelines` instead of `StreamReader`**

`StreamReader.ReadLineAsync()` allocates a string per line. `PipeReader` works against a single pooled 64KB buffer, scanning for newlines with `SequenceReader<byte>`, and calling `Advance()` to reuse the buffer. The pipeline reads an entire 1M-line file without one allocation for line reads.

**Sliding window deduplication**

`ConcurrentDictionary` + `Interlocked.Increment` = lock-free counter updates. The first occurrence per window passes through; subsequent duplicates only increment the counter. A background `Timer` evicts expired windows. The only unavoidable allocation is the dictionary key string (one per *unique* error per window).

---

## Benchmark results

Run on .NET 8, Release, AMD Ryzen 7 — 10,000 lines:

| Method | Mean | Allocated |
|---|---|---|
| `string.Split` | ~4.8 ms | 8.6 MB |
| `Regex.Match` (GeneratedRegex) | ~2.1 ms | 4.2 MB |
| **Sieve TryParse (Span)** | **~0.3 ms** | **0 B** |

*Zero bytes allocated on the Sieve hot path.*

To reproduce:
```bash
dotnet run -c Release --project src/Sieve.Benchmarks
```

---

## Running it

**Generate 1M lines and process them:**
```bash
dotnet run -c Release --project src/Sieve.Chaos
```

**Run benchmarks:**
```bash
dotnet run -c Release --project src/Sieve.Benchmarks
```

**Run tests:**
```bash
dotnet test
```

---

## If I continued this project

The remaining allocation in `SlidingWindowDeduplicator` is the string key. The fix would be a custom hash structure that accepts a `ReadOnlySpan<char>` directly — either via a `SpanEqualityComparer` backed by `MemoryMarshal`, or using `System.Runtime.CompilerServices.Unsafe` to hash the span bytes without materialising a string. I left this documented in the deduplicator source.

---

Built by **Dumisani Abrahm Baloyi** — dvmisani@gmail.com | github.com/Dvmisani
