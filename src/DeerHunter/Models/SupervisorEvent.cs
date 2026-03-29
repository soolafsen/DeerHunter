namespace DeerHunter.Models;

public sealed record SupervisorEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string EventType,
    string? ProcessName,
    string Source,
    string Message,
    IReadOnlyDictionary<string, string?> Details);
