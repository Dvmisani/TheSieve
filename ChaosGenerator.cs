using System.Text;

namespace Sieve.Chaos;

/// <summary>
/// Simulates a server meltdown across multiple threads.
///
/// Three phases mirror real incident patterns:
///   Phase 1 — Normal traffic:  Mixed log levels, varied services, realistic cadence
///   Phase 2 — Log Storm:       One error fires thousands of times per second
///                               (simulates a connection pool failure cascading)
///   Phase 3 — Chaos/Malformed: Truncated lines, null bytes, extreme lengths
///                               (simulates a dying logger writing partial buffers)
///
/// Multi-threading:
///   Each phase spawns Parallel.ForAsync workers with independent Random instances.
///   Random is NOT thread-safe — sharing one instance produces data races.
///   Each worker gets Random(seed + workerId) for deterministic reproducibility.
/// </summary>
public sealed class ChaosGenerator
{
    private static readonly string[] s_services =
    [
        "AuthService", "PaymentService", "OrderService", "InventoryService",
        "NotificationService", "ApiGateway", "CacheService", "DatabaseService",
        "SearchService", "ReportingService"
    ];

    private static readonly string[] s_errorMessages =
    [
        "NullReferenceException in RequestHandler.ProcessAsync",
        "Connection pool exhausted after 30000ms — all 100 connections in use",
        "DeadlockException detected on table [dbo].[Orders] — transaction rolled back",
        "JWT signature validation failed — token may have been tampered",
        "OutOfMemoryException thrown in DataExporter — heap size 4096MB exceeded",
        "SocketException: Connection refused by 10.0.0.52:5432 (PostgreSQL)",
        "TimeoutException: No response from PaymentGateway after 15000ms",
        "InvalidOperationException: DbContext disposed before query completed",
    ];

    private static readonly string[] s_warnMessages =
    [
        "Retry attempt 3 of 5 for order processing",
        "Cache miss ratio exceeded threshold: 42%",
        "Rate limit approaching for client 192.168.1.104",
        "Queue depth warning: 8500 pending messages",
    ];

    private static readonly string[] s_infoMessages =
    [
        "Request processed in 142ms — cache hit",
        "Health check PASSED — all dependencies reachable",
        "Background job completed: 2847 records processed",
        "Connection established to primary replica",
    ];

    // Multiple timestamp formats to stress-test the parser
    private static readonly Func<DateTime, string>[] s_timestampFormats =
    [
        dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),           // ISO-ish (most common)
        dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),          // ISO 8601 strict
        dt => dt.ToString("dd/MM/yyyy HH:mm:ss"),               // European format
        dt => dt.ToString("MM-dd-yyyy HH:mm:ss"),               // US format
        dt => ((DateTimeOffset)dt).ToUnixTimeSeconds().ToString(), // Unix epoch
    ];

    public async Task GenerateAsync(
        Stream          output,
        long            targetLines     = 1_000_000,
        int             degreeOfParallelism = 4,
        CancellationToken ct            = default)
    {
        // StreamWriter with a large buffer reduces syscall overhead significantly
        await using var writer = new StreamWriter(output, Encoding.UTF8,
            bufferSize: 131_072, leaveOpen: true);

        var baseTime = DateTime.UtcNow.AddDays(-1);

        // ── Phase 1: Normal operations (40%) ─────────────────────────────
        long normalLines = (long)(targetLines * 0.40);
        await WritePhaseAsync(writer, normalLines, degreeOfParallelism,
            (rng, timeOffset) =>
            {
                var t    = baseTime.AddMilliseconds(timeOffset * rng.Next(1, 80));
                var ts   = s_timestampFormats[rng.Next(s_timestampFormats.Length)](t);
                var svc  = s_services[rng.Next(s_services.Length)];
                var tid  = rng.Next(1, 128);

                return rng.Next(10) switch
                {
                    < 1 => $"[{ts}] [FATAL]   [ThreadId:{tid}] [Service:{svc}] {s_errorMessages[rng.Next(s_errorMessages.Length)]}",
                    < 2 => $"[{ts}] [ERROR]   [ThreadId:{tid}] [Service:{svc}] {s_errorMessages[rng.Next(s_errorMessages.Length)]}",
                    < 4 => $"[{ts}] [WARNING] [ThreadId:{tid}] [Service:{svc}] {s_warnMessages[rng.Next(s_warnMessages.Length)]}",
                    < 6 => $"[{ts}] [DEBUG]   [ThreadId:{tid}] [Service:{svc}] Entering method with params: id={rng.Next(1000)}, retry={rng.Next(5)}",
                    _   => $"[{ts}] [INFO]    [ThreadId:{tid}] [Service:{svc}] {s_infoMessages[rng.Next(s_infoMessages.Length)]}",
                };
            }, ct);

        // ── Phase 2: Log Storm — one error hammered relentlessly (40%) ────
        // This is the key scenario the deduplicator must survive
        var stormError   = s_errorMessages[0]; // Fixed — always the same error
        var stormService = "DatabaseService";
        long stormLines  = (long)(targetLines * 0.40);

        await WritePhaseAsync(writer, stormLines, degreeOfParallelism,
            (rng, timeOffset) =>
            {
                // Timestamps are milliseconds apart — high velocity
                var t  = baseTime.AddHours(12).AddMilliseconds(timeOffset * rng.Next(0, 5));
                var ts = t.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var tid = rng.Next(1, 16); // Few threads — thread pool exhausted
                return $"[{ts}] [ERROR] [ThreadId:{tid}] [Service:{stormService}] {stormError}";
            }, ct);

        // ── Phase 3: Malformed chaos (20%) ────────────────────────────────
        long chaosLines = targetLines - normalLines - stormLines;
        await WritePhaseAsync(writer, chaosLines, 1,
            (rng, _) => rng.Next(10) switch
            {
                0 => string.Empty,
                1 => "   \t   ",
                2 => $"[CORRUPTED] [ERROR no thread no service missing brackets",
                3 => $"plain unstructured log output {Guid.NewGuid()} status=500",
                4 => $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [ERROR",        // Truncated
                5 => new string('X', rng.Next(2048, 8192)),                     // Huge line
                6 => $"[{DateTime.UtcNow:yyyy-MM-dd}] no level field present",
                7 => $"\x00\x01\x02 binary garbage \xFF\xFE",                  // Bad bytes
                _ => $"{{\"level\":\"ERROR\",\"msg\":\"JSON log format not supported\"}}",
            }, ct);

        await writer.FlushAsync(ct);
    }

    private static async Task WritePhaseAsync(
        StreamWriter writer,
        long         lineCount,
        int          workers,
        Func<Random, long, string> lineFactory,
        CancellationToken ct)
    {
        long written    = 0L;
        var  writeLock  = new SemaphoreSlim(1, 1);
        long linesPerWorker = lineCount / workers;

        await Parallel.ForAsync(0, workers, ct, async (workerId, innerCt) =>
        {
            // Each worker gets its own Random — Random is NOT thread-safe
            var rng   = new Random(workerId * 31337 + Environment.TickCount);
            var batch = new StringBuilder(capacity: 131_072);

            for (long i = 0; i < linesPerWorker && !innerCt.IsCancellationRequested; i++)
            {
                batch.AppendLine(lineFactory(rng, Interlocked.Increment(ref written)));

                // Flush in batches of 1000 to amortise lock contention
                if (batch.Length >= 65_536)
                {
                    await writeLock.WaitAsync(innerCt).ConfigureAwait(false);
                    try   { await writer.WriteAsync(batch, innerCt).ConfigureAwait(false); }
                    finally { writeLock.Release(); batch.Clear(); }
                }
            }

            if (batch.Length > 0)
            {
                await writeLock.WaitAsync(innerCt).ConfigureAwait(false);
                try   { await writer.WriteAsync(batch, innerCt).ConfigureAwait(false); }
                finally { writeLock.Release(); }
            }
        });
    }
}
