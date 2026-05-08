namespace Sieve.Core.Parser;

/// <summary>
/// A zero-allocation log entry.
///
/// WHY ref struct?
///   A ref struct is guaranteed to live on the STACK only. The CLR forbids
///   boxing it, storing it in arrays, or passing it to async methods.
///   This means no GC pressure regardless of how many entries we parse —
///   each one lives and dies within a single stack frame.
///
/// WHY ReadOnlySpan&lt;char&gt; fields?
///   Spans are fat pointers: a managed ref + length. They point INTO the
///   original string/buffer memory — no substrings are ever created.
///   If the caller processes 1 million log lines, zero intermediate strings
///   are allocated for timestamp, level, service, or message.
/// </summary>
public readonly ref struct LogEntry
{
    /// <summary>Raw timestamp slice — caller decides whether to parse it.</summary>
    public readonly ReadOnlySpan<char> RawTimestamp;

    /// <summary>Log level, decoded to an enum during parsing.</summary>
    public readonly LogLevel Level;

    /// <summary>Thread ID slice, e.g. "42" from [ThreadId:42].</summary>
    public readonly ReadOnlySpan<char> ThreadId;

    /// <summary>Service name slice, e.g. "AuthService" from [Service:AuthService].</summary>
    public readonly ReadOnlySpan<char> ServiceName;

    /// <summary>Everything after the structured headers.</summary>
    public readonly ReadOnlySpan<char> Message;

    // Internal constructor enforces creation only via LogParser.TryParse —
    // consumers cannot construct a LogEntry directly.
    internal LogEntry(
        ReadOnlySpan<char> rawTimestamp,
        LogLevel           level,
        ReadOnlySpan<char> threadId,
        ReadOnlySpan<char> serviceName,
        ReadOnlySpan<char> message)
    {
        RawTimestamp = rawTimestamp;
        Level        = level;
        ThreadId     = threadId;
        ServiceName  = serviceName;
        Message      = message;
    }

    /// <summary>
    /// Materialises a heap string from the Message span.
    /// Only call this when you intend to store or log the value — it allocates.
    /// On the hot path, prefer Message.Equals(...) or Message.Contains(...).
    /// </summary>
    public string MessageAsString() => Message.ToString();
}
