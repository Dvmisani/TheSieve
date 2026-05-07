using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Order;
using Sieve.Core.Parser;
using System.Text.RegularExpressions;

namespace Sieve.Benchmarks;

/// <summary>
/// BenchmarkDotNet comparison: Standard (Regex + string.Split) vs Sieve (Span).
///
/// [MemoryDiagnoser]  — captures Gen0/1/2 GC collections and allocated bytes
/// [ThreadingDiagnoser] — shows completed work items and lock contentions
///
/// Run in Release mode only:
///   dotnet run -c Release --project src/Sieve.Benchmarks
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Error", "StdDev", "RatioSD", "Gen1", "Gen2")]
public class ParserBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int LineCount;

    private string[] _validLines   = null!;
    private string[] _mixedLines   = null!;  // 20% malformed

    // [GeneratedRegex] compiles the regex to IL at build time — faster than
    // Regex.Match() with a runtime-compiled pattern, but still allocates
    // Match objects and Group strings on every call.
    [GeneratedRegex(
        @"^\[(?<ts>[^\]]+)\]\s+\[(?<lvl>[^\]]+)\]\s+\[ThreadId:(?<tid>\d+)\]\s+\[Service:(?<svc>[^\]]+)\]\s+(?<msg>.+)$",
        RegexOptions.Compiled)]
    private static partial Regex LogRegex();

    [GlobalSetup]
    public void Setup()
    {
        _validLines = BuildLines(LineCount, malformedRatio: 0.00);
        _mixedLines = BuildLines(LineCount, malformedRatio: 0.20);
    }

    // ── Standard approach: GeneratedRegex ────────────────────────────────

    [Benchmark(Baseline = true, Description = "Regex.Match — valid lines")]
    public int Standard_Regex_Valid()
    {
        int count = 0;
        foreach (var line in _validLines)
        {
            var m = LogRegex().Match(line);
            if (!m.Success) continue;
            // Force string allocations — simulates real consumer behaviour
            _ = m.Groups["ts"].Value;
            _ = m.Groups["lvl"].Value;
            _ = m.Groups["svc"].Value;
            _ = m.Groups["msg"].Value;
            count++;
        }
        return count;
    }

    [Benchmark(Description = "string.Split — valid lines")]
    public int Standard_Split_Valid()
    {
        int count = 0;
        foreach (var line in _validLines)
        {
            // The classic naïve approach — allocates string[] + substrings
            var parts = line.Split(new[] { "] [", "[", "]" },
                                   StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4) count++;
        }
        return count;
    }

    // ── Sieve approach: Span<T> ───────────────────────────────────────────

    [Benchmark(Description = "Sieve.TryParse — valid lines")]
    public int Sieve_TryParse_Valid()
    {
        int count = 0;
        foreach (var line in _validLines)
        {
            // AsSpan() is a zero-allocation view into the existing string memory
            if (LogParser.TryParse(line.AsSpan(), out var entry))
            {
                _ = entry.Level;        // Stack read — no allocation
                _ = entry.Message.Length; // Span length — no allocation
                count++;
            }
        }
        return count;
    }

    [Benchmark(Description = "Regex.Match — mixed lines")]
    public int Standard_Regex_Mixed()
    {
        int count = 0;
        foreach (var line in _mixedLines)
        {
            if (LogRegex().Match(line).Success) count++;
        }
        return count;
    }

    [Benchmark(Description = "Sieve.TryParse — mixed lines")]
    public int Sieve_TryParse_Mixed()
    {
        int count = 0;
        foreach (var line in _mixedLines)
        {
            if (LogParser.TryParse(line.AsSpan(), out _)) count++;
        }
        return count;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string[] BuildLines(int count, double malformedRatio)
    {
        var rng      = new Random(0xDEADBEEF);
        var lines    = new string[count];
        int badCount = (int)(count * malformedRatio);

        var malformed = new string[]
        {
            "",
            "   ",
            "plain text no structure",
            "[BAD_TIMESTAMP] [ERROR missing closing bracket",
        };

        for (int i = 0; i < count; i++)
        {
            lines[i] = i < badCount
                ? malformed[rng.Next(malformed.Length)]
                : $"[2024-{rng.Next(1,13):D2}-{rng.Next(1,29):D2} "     +
                  $"{rng.Next(0,24):D2}:{rng.Next(0,60):D2}:{rng.Next(0,60):D2}.{rng.Next(0,1000):D3}] " +
                  $"[ERROR] [ThreadId:{rng.Next(1,128)}] "               +
                  $"[Service:AuthService] Connection pool exhausted after 30000ms";
        }

        // Fisher-Yates shuffle for realistic mixed ordering
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        return lines;
    }
}
