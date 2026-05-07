using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Sieve.Core.Deduplication;

/// <summary>
/// Sliding-window deduplicator that survives Log Storms.
///
/// Design rationale:
///   During a Log Storm, a single error can fire 5,000+ times per second.
///   Without deduplication, downstream consumers (databases, alerting systems)
///   would be flooded with identical writes — causing a cascade failure.
///
///   This class intercepts at the source: the FIRST occurrence is passed
///   through, subsequent duplicates within the window only increment an
///   atomic counter. A background timer evicts stale windows.
///
/// Thread safety:
///   ConcurrentDictionary handles concurrent GetOrAdd from multiple threads.
///   Interlocked.Increment provides lock-free atomic counter updates.
///   No lock {} blocks exist in the hot path.
/// </summary>
public sealed class SlidingWindowDeduplicator : IDisposable
{
    private readonly ConcurrentDictionary<string, DedupBucket> _buckets = new(
        concurrencyLevel: Environment.ProcessorCount,
        capacity: 1024);

    private readonly TimeSpan _windowDuration;
    private readonly Timer    _evictionTimer;
    private volatile bool     _disposed;

    public SlidingWindowDeduplicator(TimeSpan? windowDuration = null)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromSeconds(1);
        _evictionTimer  = new Timer(EvictExpiredBuckets, null,
                                    _windowDuration, _windowDuration);
    }

    /// <summary>
    /// Records an occurrence of a log message.
    ///
    /// Returns true only on the FIRST occurrence in the current window.
    /// The caller should only forward the entry downstream when this is true.
    ///
    /// NOTE: This method has one unavoidable allocation — the dictionary key.
    ///   string.Concat is used instead of interpolation to avoid the
    ///   intermediate FormattableString allocation. This is the minimum
    ///   possible allocation for a string-keyed dictionary.
    ///   Alternative: a custom Span-keyed structure using unsafe hash —
    ///   see future work in README.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRecord(ReadOnlySpan<char> message, LogLevel level, out long count)
    {
        // One allocation per unique error per window — acceptable off hot path
        var key = string.Concat(((byte)level).ToString(), "|", message);

        var bucket = _buckets.GetOrAdd(key, static _ => new DedupBucket());
        count = Interlocked.Increment(ref bucket.HitCount);

        return count == 1L;
    }

    /// <summary>
    /// Returns a snapshot of all deduplicated entries with their hit counts.
    /// Intended for periodic reporting, not hot-path processing.
    /// </summary>
    public IReadOnlyList<DedupSummary> Snapshot()
    {
        var results = new List<DedupSummary>(_buckets.Count);
        foreach (var (key, bucket) in _buckets)
        {
            results.Add(new DedupSummary(
                Key:       key,
                HitCount:  Interlocked.Read(ref bucket.HitCount),
                FirstSeen: bucket.FirstSeen));
        }
        return results;
    }

    private void EvictExpiredBuckets(object? _)
    {
        if (_disposed) return;

        var cutoff = DateTimeOffset.UtcNow - _windowDuration;
        foreach (var key in _buckets.Keys)
        {
            if (_buckets.TryGetValue(key, out var b) && b.FirstSeen < cutoff)
                _buckets.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _evictionTimer.Dispose();
    }
}

/// <summary>
/// Mutable bucket — must be a class so that ConcurrentDictionary holds a
/// stable reference and Interlocked can update HitCount in place.
/// A struct would be copied on GetOrAdd, making the increment a no-op.
/// </summary>
internal sealed class DedupBucket
{
    // Not volatile — Interlocked operations carry full memory barriers
    public long HitCount;
    public readonly DateTimeOffset FirstSeen = DateTimeOffset.UtcNow;
}

public readonly record struct DedupSummary(
    string         Key,
    long           HitCount,
    DateTimeOffset FirstSeen);
