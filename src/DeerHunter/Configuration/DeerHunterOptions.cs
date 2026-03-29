using System.ComponentModel.DataAnnotations;

namespace DeerHunter.Configuration;

public sealed class DeerHunterOptions
{
    [Range(10, 100_000)]
    public int EventBufferSize { get; set; } = 1_000;

    [Required]
    public string JournalPath { get; set; } = "state/events.jsonl";

    [Required]
    public ApiOptions Api { get; set; } = new();

    public List<ManagedProcessOptions> Processes { get; set; } = [];
}

public sealed class ApiOptions
{
    public string[] Urls { get; set; } = ["http://127.0.0.1:5078"];
}

public sealed class ManagedProcessOptions
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [Required]
    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public bool AutoStart { get; set; } = true;

    public bool IsHelper { get; set; }

    public string? HelperFor { get; set; }

    public ManagedProcessPriority Priority { get; set; } = ManagedProcessPriority.Normal;

    public RestartPolicyOptions Restart { get; set; } = new();

    public List<ExternalLogOptions> ExternalLogs { get; set; } = [];

    public List<SignalRuleOptions> Rules { get; set; } = [];

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RestartPolicyOptions
{
    public bool Enabled { get; set; } = true;

    [Range(0, 3600)]
    public int DelaySeconds { get; set; } = 2;

    [Range(0, 10_000)]
    public int MaxAttempts { get; set; }
}

public sealed class ExternalLogOptions
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Path { get; set; } = string.Empty;

    public ExternalLogStartPosition StartPosition { get; set; } = ExternalLogStartPosition.End;
}

public sealed class SignalRuleOptions
{
    [Required]
    public string Id { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public LogMatchType MatchType { get; set; } = LogMatchType.Contains;

    [Required]
    public string Pattern { get; set; } = string.Empty;

    public bool CaseSensitive { get; set; }

    public LogSourceType Source { get; set; } = LogSourceType.Any;

    public string? ExternalLogName { get; set; }

    [Range(0, 3600)]
    public int CooldownSeconds { get; set; }

    public List<RuleActionOptions> Actions { get; set; } = [];
}

public sealed class RuleActionOptions
{
    public RuleActionType Type { get; set; }

    public string? TargetProcess { get; set; }

    public ManagedProcessPriority? Priority { get; set; }

    public ManagedProcessCondition? Condition { get; set; }
}

public enum ManagedProcessLifecycle
{
    Disabled,
    Stopped,
    Starting,
    Running,
    Restarting,
    Backoff,
    Stopping,
    Failed
}

public enum ManagedProcessCondition
{
    Unknown,
    Healthy,
    Degraded,
    Blocked
}

public enum ManagedProcessPriority
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    RealTime
}

public enum LogMatchType
{
    Contains,
    Regex
}

public enum LogSourceType
{
    Any,
    Stdout,
    Stderr,
    ExternalLog
}

public enum ExternalLogStartPosition
{
    Beginning,
    End
}

public enum RuleActionType
{
    Kill,
    Restart,
    SetPriority,
    RaisePriority,
    LowerPriority,
    StartHelper,
    StopHelper,
    SetCondition
}
