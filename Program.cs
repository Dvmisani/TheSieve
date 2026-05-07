using Sieve.Chaos;
using Sieve.Core.Deduplication;
using Sieve.Core.Pipeline;
using System.Diagnostics;

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║          THE SIEVE — CHAOS MODE          ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();

const long targetLines = 1_000_000;
const string outputPath = "chaos_logs.txt";

// ── Step 1: Generate the chaos ────────────────────────────────────────────
Console.Write($"[1/2] Generating {targetLines:N0} log lines...");
var sw = Stopwatch.StartNew();

var generator = new ChaosGenerator();
await using (var fileStream = File.Create(outputPath))
    await generator.GenerateAsync(fileStream, targetLines, degreeOfParallelism: 4);

sw.Stop();
var fileSize = new FileInfo(outputPath).Length / 1024.0 / 1024.0;
Console.WriteLine($" done in {sw.Elapsed.TotalSeconds:F2}s  ({fileSize:F1} MB)");

// ── Step 2: Process through The Sieve ────────────────────────────────────
Console.Write("[2/2] Processing through The Sieve...");
sw.Restart();

using var deduplicator = new SlidingWindowDeduplicator(TimeSpan.FromSeconds(1));
ParseStats stats = default;

var pipeline = new SievePipeline(
    deduplicator,
    onNewError: (entry, count) =>
    {
        // Only the FIRST occurrence per window reaches here
        // In production: write to alerting system, database, etc.
    },
    onStats: s => stats = s);

await using (var readStream = File.OpenRead(outputPath))
    await pipeline.ProcessAsync(readStream);

sw.Stop();
double throughput = stats.TotalLines / sw.Elapsed.TotalSeconds;

Console.WriteLine($" done in {sw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine();
Console.WriteLine("══════════════ RESULTS ══════════════");
Console.WriteLine($"  Total lines processed : {stats.TotalLines,12:N0}");
Console.WriteLine($"  Successfully parsed   : {stats.ParsedLines,12:N0}  ({stats.ParsedLines * 100.0 / stats.TotalLines:F1}%)");
Console.WriteLine($"  Errors detected       : {stats.ErrorCount,12:N0}");
Console.WriteLine($"  Duplicates suppressed : {stats.DedupHits,12:N0}");
Console.WriteLine($"  Throughput            : {throughput,12:N0} lines/sec");
Console.WriteLine();

var summary = deduplicator.Snapshot();
Console.WriteLine($"  Unique error buckets  : {summary.Count}");
foreach (var bucket in summary.OrderByDescending(b => b.HitCount).Take(5))
    Console.WriteLine($"  [{bucket.HitCount,8:N0}x] {bucket.Key[..Math.Min(70, bucket.Key.Length)]}");

Console.WriteLine("═════════════════════════════════════");
