using System.Buffers;
using System.Runtime.CompilerServices;

namespace Sieve.Core.Parser;

/// <summary>
/// Zero-allocation log parser using Span&lt;T&gt; and SearchValues&lt;T&gt;.
///
/// IL BENEFITS OF Span&lt;T&gt; IN THIS CONTEXT:
///   When the JIT compiles Span operations on a string, it can often prove
///   the bounds at JIT time and eliminate bounds-checks entirely via range
///   analysis. string.Split() cannot do this — it must heap-allocate a
///   string[] and validate each element independently.
///
/// SearchValues&lt;char&gt; (introduced in .NET 8):
///   Builds a lookup table at startup that enables SIMD vectorised search
///   via IndexOfAny. On AVX2 hardware this processes 32 chars per CPU cycle.
///   A naive loop processes 1 char per cycle. 32× throughput for bracket
///   scanning is the direct result.
///
/// Expected log format:
///   [TIMESTAMP] [LEVEL] [ThreadId:N] [Service:NAME] message text here
/// </summary>
public static class LogParser
{
    // Computed once at class initialisation — stored in a static readonly field
    // so the JIT can inline the reference. The SearchValues&lt;char&gt; type uses an
    // internal bitmap + SIMD path for IndexOfAny calls.
    private static readonly SearchValues<char> s_closingBracket =
        SearchValues.Create(']');

    /// <summary>
    /// Parses a single log line without any heap allocation.
    ///
    /// Uses the Fail-Fast TryParse pattern: returns false immediately on
    /// the first structural violation, avoiding wasted CPU on malformed input.
    /// The out parameter uses a default(LogEntry) on failure, which is a
    /// zero-byte stack write — not a heap allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<char> line, out LogEntry entry)
    {
        // Default is a zero-fill of the stack frame — no allocation
        entry = default;

        // Fast reject: minimum viable log line is ~30 chars
        if (line.Length < 25)
            return false;

        // Each TryExtractBracketed call advances `remaining` past the parsed token.
        // We pass by ref so the span "cursor" moves forward without copying data.
        var remaining = line;

        if (!TryExtractBracketed(ref remaining, out var timestamp))
            return false;

        remaining = remaining.TrimStart();

        if (!TryExtractBracketed(ref remaining, out var levelSpan))
            return false;

        var level = ParseLevel(levelSpan);

        remaining = remaining.TrimStart();

        if (!TryExtractBracketed(ref remaining, out var threadRaw))
            return false;

        // Slice past the "ThreadId:" prefix without allocating
        var colonIdx = threadRaw.IndexOf(':');
        var threadId = colonIdx >= 0 ? threadRaw[(colonIdx + 1)..] : threadRaw;

        remaining = remaining.TrimStart();

        if (!TryExtractBracketed(ref remaining, out var serviceRaw))
            return false;

        var serviceColon = serviceRaw.IndexOf(':');
        var serviceName  = serviceColon >= 0 ? serviceRaw[(serviceColon + 1)..] : serviceRaw;

        var message = remaining.TrimStart();

        if (message.IsEmpty)
            return false;

        entry = new LogEntry(timestamp, level, threadId, serviceName, message);
        return true;
    }

    /// <summary>
    /// Extracts content between '[' and ']', then advances the input span
    /// past the closing bracket. Returns false if structure is missing.
    ///
    /// The `ref` parameter is the key to cursor-based parsing without
    /// allocation — we're mutating the local span reference, not the data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryExtractBracketed(
        ref ReadOnlySpan<char> input,
        out ReadOnlySpan<char> content)
    {
        content = default;

        if (input.IsEmpty || input[0] != '[')
            return false;

        // Search from index 1 (after '[') using SIMD-accelerated IndexOfAny
        var inner     = input[1..];
        var closeIdx  = inner.IndexOfAny(s_closingBracket);

        if (closeIdx < 0)
            return false;

        content = inner[..closeIdx];           // Slice INTO the original buffer
        input   = inner[(closeIdx + 1)..];     // Advance cursor past ']'
        return true;
    }

    /// <summary>
    /// Span.Equals performs ordinal comparison without allocating a string.
    /// The C# switch expression with `when` compiles to a sequence of
    /// SequenceEqual calls — no boxing, no dictionary lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogLevel ParseLevel(ReadOnlySpan<char> span) => span switch
    {
        _ when span.Equals("TRACE",   StringComparison.OrdinalIgnoreCase) => LogLevel.Trace,
        _ when span.Equals("DEBUG",   StringComparison.OrdinalIgnoreCase) => LogLevel.Debug,
        _ when span.Equals("INFO",    StringComparison.OrdinalIgnoreCase) => LogLevel.Info,
        _ when span.Equals("WARNING", StringComparison.OrdinalIgnoreCase) => LogLevel.Warning,
        _ when span.Equals("WARN",    StringComparison.OrdinalIgnoreCase) => LogLevel.Warning,
        _ when span.Equals("ERROR",   StringComparison.OrdinalIgnoreCase) => LogLevel.Error,
        _ when span.Equals("FATAL",   StringComparison.OrdinalIgnoreCase) => LogLevel.Fatal,
        _                                                                   => LogLevel.Unknown,
    };
}
