using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using Sieve.Core.Deduplication;
using Sieve.Core.Parser;

namespace Sieve.Core.Pipeline;

/// <summary>
/// High-throughput processing pipeline using System.IO.Pipelines.
///
/// WHY System.IO.Pipelines INSTEAD OF StreamReader.ReadLineAsync?
///   StreamReader.ReadLineAsync allocates a new string for EVERY line.
///   At 1M lines, that is 1M string allocations just for line reads —
///   before parsing even begins.
///
///   PipeReader works differently:
///     1. The Pipe owns a single pooled buffer (64KB from ArrayPool).
///     2. We scan for newlines using SIMD-accelerated SequenceReader.
///     3. We hand a ReadOnlySpan directly to LogParser.TryParse.
///     4. We call Advance() to mark bytes consumed — the buffer is reused.
///
///   Net result: zero per-line allocations for reading OR parsing.
/// </summary>
public sealed class SievePipeline
{
    private readonly SlidingWindowDeduplicator _deduplicator;
    private readonly Action<LogEntry, long>?   _onNewError;
    private readonly Action<ParseStats>?        _onStats;

    public SievePipeline(
        SlidingWindowDeduplicator deduplicator,
        Action<LogEntry, long>?   onNewError = null,
        Action<ParseStats>?       onStats    = null)
    {
        _deduplicator = deduplicator;
        _onNewError   = onNewError;
        _onStats      = onStats;
    }

    public async Task ProcessAsync(Stream source, CancellationToken ct = default)
    {
        var reader = PipeReader.Create(source, new StreamPipeReaderOptions(
            bufferSize: 65_536, minimumReadSize: 4_096));

        long totalLines  = 0L;
        long parsedLines = 0L;
        long dedupHits   = 0L;
        long errorCount  = 0L;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, out var line))
                {
                    totalLines++;
                    ProcessLine(line, ref parsedLines, ref dedupHits, ref errorCount);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            _onStats?.Invoke(new ParseStats(totalLines, parsedLines, dedupHits, errorCount));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer,
                                    out ReadOnlySequence<byte> line)
    {
        var seqReader = new SequenceReader<byte>(buffer);
        if (seqReader.TryReadTo(out line, (byte)'\n'))
        {
            buffer = buffer.Slice(seqReader.Position);
            return true;
        }
        line = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessLine(ReadOnlySequence<byte> lineBytes,
                              ref long parsed, ref long dedup, ref long errors)
    {
        ReadOnlySpan<byte> bytes = lineBytes.IsSingleSegment
            ? lineBytes.FirstSpan
            : lineBytes.ToArray();

        const int stackThreshold = 1024;
        int charCount = Encoding.UTF8.GetCharCount(bytes);

        if (charCount <= stackThreshold)
        {
            Span<char> chars = stackalloc char[charCount];
            Encoding.UTF8.GetChars(bytes, chars);
            ParseAndRoute(chars, ref parsed, ref dedup, ref errors);
        }
        else
        {
            char[] rented = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                var chars = rented.AsSpan(0, charCount);
                Encoding.UTF8.GetChars(bytes, chars);
                ParseAndRoute(chars, ref parsed, ref dedup, ref errors);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ParseAndRoute(ReadOnlySpan<char> chars,
                                ref long parsed, ref long dedup, ref long errors)
    {
        if (!LogParser.TryParse(chars, out var entry)) return;

        parsed++;

        if (entry.Level < LogLevel.Error) return;

        errors++;

        if (_deduplicator.TryRecord(entry.Message, entry.Level, out var count))
            _onNewError?.Invoke(entry, count);
        else
            dedup++;
    }
}

public readonly record struct ParseStats(
    long TotalLines,
    long ParsedLines,
    long DedupHits,
    long ErrorCount);
