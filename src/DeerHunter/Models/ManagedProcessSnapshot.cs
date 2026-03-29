using DeerHunter.Configuration;

namespace DeerHunter.Models;

public sealed record ManagedProcessSnapshot(
    string Name,
    string Description,
    bool IsHelper,
    string? HelperFor,
    bool AutoStart,
    ManagedProcessLifecycle Lifecycle,
    ManagedProcessCondition Condition,
    ManagedProcessPriority Priority,
    int? ProcessId,
    int RestartAttempts,
    int? LastExitCode,
    DateTimeOffset? LastStartedAtUtc,
    DateTimeOffset? LastStoppedAtUtc,
    string? LastError);
