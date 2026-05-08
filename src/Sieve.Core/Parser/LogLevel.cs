namespace Sieve.Core.Parser;

/// <summary>
/// Stored as a byte (1 byte on stack) rather than int (4 bytes).
/// When packed into LogEntry on the stack, this matters at scale.
/// </summary>
public enum LogLevel : byte
{
    Unknown = 0,
    Trace   = 1,
    Debug   = 2,
    Info    = 3,
    Warning = 4,
    Error   = 5,
    Fatal   = 6,
}
