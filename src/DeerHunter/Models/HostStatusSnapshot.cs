namespace DeerHunter.Models;

public sealed record HostStatusSnapshot(
    string SupervisionState,
    bool SupervisionPaused,
    string ConfigPath,
    DateTimeOffset StartedAtUtc,
    long UptimeSeconds,
    int ManagedProcessCount,
    int HelperCount,
    int BufferedEventCount);
