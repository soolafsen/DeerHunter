using DeerHunter.Configuration;
using DeerHunter.Models;
using Microsoft.Extensions.Options;

namespace DeerHunter.Services;

public sealed class SupervisorCoordinator : IHostedService
{
    private readonly EventStore _eventStore;
    private readonly EventJournal _eventJournal;
    private readonly IHostEnvironment _environment;
    private readonly HostRuntimeState _hostRuntimeState;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<SupervisorCoordinator> _logger;
    private readonly Dictionary<string, ManagedProcessAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public SupervisorCoordinator(
        IOptions<DeerHunterOptions> options,
        EventStore eventStore,
        EventJournal eventJournal,
        IHostEnvironment environment,
        HostRuntimeState hostRuntimeState,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        ILogger<SupervisorCoordinator> logger)
    {
        _eventStore = eventStore;
        _eventJournal = eventJournal;
        _environment = environment;
        _hostRuntimeState = hostRuntimeState;
        _applicationLifetime = applicationLifetime;
        _logger = logger;

        foreach (var process in options.Value.Processes)
        {
            _agents[process.Name] = new ManagedProcessAgent(process, this, environment, loggerFactory.CreateLogger<ManagedProcessAgent>());
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents.Values)
        {
            agent.Initialize();
        }

        foreach (var agent in _agents.Values.Where(static item => item.Options.AutoStart))
        {
            await agent.StartAsync("auto start", cancellationToken);
        }

        if (_agents.Count == 0)
        {
            _logger.LogInformation("Supervisor is idle with zero managed processes.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents.Values)
        {
            await agent.DisposeAsync(cancellationToken);
        }
    }

    public IReadOnlyList<ManagedProcessSnapshot> GetSnapshots() => _eventStore.GetSnapshots();

    public ManagedProcessSnapshot? GetSnapshot(string name) => _eventStore.GetSnapshot(name);

    public IReadOnlyList<SupervisorEvent> GetRecentEvents(int take) => _eventStore.GetRecent(take);

    public bool IsSupervisionPaused => _hostRuntimeState.IsSupervisionPaused;

    public HostStatusSnapshot GetHostStatus()
    {
        var managedProcessCount = 0;
        var helperCount = 0;

        foreach (var agent in _agents.Values)
        {
            if (agent.Options.IsHelper)
            {
                helperCount++;
            }
            else
            {
                managedProcessCount++;
            }
        }

        return new HostStatusSnapshot(
            SupervisionState: _hostRuntimeState.IsSupervisionPaused ? "Paused" : "Running",
            SupervisionPaused: _hostRuntimeState.IsSupervisionPaused,
            ConfigPath: _hostRuntimeState.ConfigPath,
            StartedAtUtc: _hostRuntimeState.StartedAtUtc,
            UptimeSeconds: Math.Max(0, (long)(DateTimeOffset.UtcNow - _hostRuntimeState.StartedAtUtc).TotalSeconds),
            ManagedProcessCount: managedProcessCount,
            HelperCount: helperCount,
            BufferedEventCount: _eventStore.GetBufferedEventCount());
    }

    public HostStatusSnapshot PauseSupervision(string source, string reason)
    {
        _hostRuntimeState.PauseSupervision();
        RecordEvent(
            "host.supervision.paused",
            processName: null,
            source,
            "Host supervision paused.",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["supervisionState"] = "Paused"
            });

        return GetHostStatus();
    }

    public HostStatusSnapshot ResumeSupervision(string source, string reason)
    {
        _hostRuntimeState.ResumeSupervision();
        RecordEvent(
            "host.supervision.resumed",
            processName: null,
            source,
            "Host supervision resumed.",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["supervisionState"] = "Running"
            });

        return GetHostStatus();
    }

    public HostStatusSnapshot ReloadConfiguration(string source, string reason)
    {
        RecordEvent(
            "host.configuration.reload.requested",
            processName: null,
            source,
            "Host configuration reload requested.",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["configPath"] = _hostRuntimeState.ConfigPath
            });

        return GetHostStatus();
    }

    public HostStatusSnapshot RequestCleanShutdown(string source, string reason)
    {
        _hostRuntimeState.MarkShutdownRequested();
        RecordEvent(
            "host.shutdown.requested",
            processName: null,
            source,
            "Host clean shutdown requested.",
            new Dictionary<string, string?>
            {
                ["reason"] = reason
            });

        _ = Task.Run(_applicationLifetime.StopApplication);
        return GetHostStatus();
    }

    public Task<ManagedProcessSnapshot?> StartProcessAsync(string name, string reason, CancellationToken cancellationToken)
        => WithAgentAsync(name, agent => agent.StartAsync(reason, cancellationToken));

    public Task<ManagedProcessSnapshot?> StopProcessAsync(string name, string reason, CancellationToken cancellationToken)
        => WithAgentAsync(name, agent => agent.StopAsync(reason, cancellationToken: cancellationToken));

    public Task<ManagedProcessSnapshot?> RestartProcessAsync(string name, string reason, CancellationToken cancellationToken)
        => WithAgentAsync(name, agent => agent.RestartAsync(reason, cancellationToken));

    public Task<ManagedProcessSnapshot?> SetPriorityAsync(string name, ManagedProcessPriority priority, string reason, CancellationToken cancellationToken)
        => WithAgentAsync(name, agent => agent.SetPriorityAsync(priority, reason, cancellationToken));

    public async Task ExecuteRuleActionsAsync(string sourceProcessName, SignalRuleOptions rule, CancellationToken cancellationToken)
    {
        if (_hostRuntimeState.IsSupervisionPaused)
        {
            RecordEvent(
                "rule.action.skipped",
                sourceProcessName,
                "supervisor",
                $"Rule {rule.Id} skipped while supervision is paused.",
                new Dictionary<string, string?>
                {
                    ["ruleId"] = rule.Id,
                    ["reason"] = "supervision paused"
                });
            return;
        }

        foreach (var action in rule.Actions)
        {
            try
            {
                switch (action.Type)
                {
                    case RuleActionType.Kill:
                        await StopProcessForRuleAsync(action.TargetProcess ?? sourceProcessName, sourceProcessName, rule.Id, action.Type, cancellationToken);
                        break;
                    case RuleActionType.Restart:
                        await RestartProcessForRuleAsync(action.TargetProcess ?? sourceProcessName, sourceProcessName, rule.Id, action.Type, cancellationToken);
                        break;
                    case RuleActionType.SetPriority:
                        if (action.Priority is null)
                        {
                            RecordRuleFailure(sourceProcessName, rule.Id, action.Type, "Rule is missing a priority value.");
                            break;
                        }

                        await SetPriorityForRuleAsync(action.TargetProcess ?? sourceProcessName, action.Priority.Value, sourceProcessName, rule.Id, action.Type, cancellationToken);
                        break;
                    case RuleActionType.RaisePriority:
                        await AdjustPriorityAsync(action.TargetProcess ?? sourceProcessName, raise: true, rule.Id, cancellationToken);
                        break;
                    case RuleActionType.LowerPriority:
                        await AdjustPriorityAsync(action.TargetProcess ?? sourceProcessName, raise: false, rule.Id, cancellationToken);
                        break;
                    case RuleActionType.StartHelper:
                        await StartHelperForRuleAsync(action.TargetProcess, sourceProcessName, rule.Id, action.Type, cancellationToken);
                        break;
                    case RuleActionType.StopHelper:
                        await StopHelperForRuleAsync(action.TargetProcess, sourceProcessName, rule.Id, action.Type, cancellationToken);
                        break;
                    case RuleActionType.SetCondition:
                        if (action.Condition is null)
                        {
                            RecordRuleFailure(sourceProcessName, rule.Id, action.Type, "Rule is missing a condition value.");
                            break;
                        }

                        if (!_agents.TryGetValue(action.TargetProcess ?? sourceProcessName, out var conditionAgent))
                        {
                            RecordRuleFailure(sourceProcessName, rule.Id, action.Type, "Rule referenced an unknown process.", action.TargetProcess ?? sourceProcessName);
                            break;
                        }

                        conditionAgent.SetCondition(action.Condition.Value, $"rule {rule.Id}");
                        break;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Rule action {ActionType} failed for {ProcessName}", action.Type, sourceProcessName);
                RecordEvent("rule.action.failed", sourceProcessName, "rule", $"Rule {rule.Id} action {action.Type} failed.",
                    new Dictionary<string, string?>
                    {
                        ["ruleId"] = rule.Id,
                        ["action"] = action.Type.ToString(),
                        ["error"] = exception.Message
                    });
            }
        }
    }

    public SupervisorEvent RecordEvent(string eventType, string? processName, string source, string message, IReadOnlyDictionary<string, string?>? details = null)
    {
        var supervisorEvent = _eventStore.Record(eventType, processName, source, message, details);
        _logger.Log(GetLogLevel(supervisorEvent), "Supervisor event {EventType} process={ProcessName} source={Source} message={Message} details={Details}",
            supervisorEvent.EventType,
            supervisorEvent.ProcessName ?? "<none>",
            supervisorEvent.Source,
            supervisorEvent.Message,
            supervisorEvent.Details);
        _ = _eventJournal.EnqueueAsync(supervisorEvent);
        return supervisorEvent;
    }

    public void UpdateSnapshot(ManagedProcessSnapshot snapshot)
    {
        _eventStore.UpsertSnapshot(snapshot);
    }

    internal string ResolvePath(string path) => Path.GetFullPath(path, _environment.ContentRootPath);

    internal bool ShouldSuppressAutomaticRestart() => _hostRuntimeState.IsSupervisionPaused;

    private async Task<ManagedProcessSnapshot?> AdjustPriorityAsync(string processName, bool raise, string ruleId, CancellationToken cancellationToken)
    {
        var snapshot = GetSnapshot(processName);
        if (snapshot is null)
        {
            RecordRuleFailure(processName, ruleId, raise ? RuleActionType.RaisePriority : RuleActionType.LowerPriority, "Rule referenced an unknown process.", processName);
            return null;
        }

        var values = Enum.GetValues<ManagedProcessPriority>();
        var index = Array.IndexOf(values, snapshot.Priority);
        index = raise ? Math.Min(values.Length - 1, index + 1) : Math.Max(0, index - 1);
        return await SetPriorityAsync(processName, values[index], $"rule {ruleId} {(raise ? "raise" : "lower")} priority", cancellationToken);
    }

    private async Task<ManagedProcessSnapshot?> WithAgentAsync(string name, Func<ManagedProcessAgent, Task<ManagedProcessSnapshot>> action)
    {
        if (!_agents.TryGetValue(name, out var agent))
        {
            return null;
        }

        return await action(agent);
    }

    private async Task StartHelperForRuleAsync(string? helperName, string sourceProcessName, string ruleId, RuleActionType actionType, CancellationToken cancellationToken)
    {
        if (!TryGetHelperAgent(helperName, sourceProcessName, ruleId, actionType, out var helperAgent))
        {
            return;
        }

        await helperAgent.StartAsync($"rule {ruleId} start helper", cancellationToken);
    }

    private async Task StopHelperForRuleAsync(string? helperName, string sourceProcessName, string ruleId, RuleActionType actionType, CancellationToken cancellationToken)
    {
        if (!TryGetHelperAgent(helperName, sourceProcessName, ruleId, actionType, out var helperAgent))
        {
            return;
        }

        await helperAgent.StopAsync($"rule {ruleId} stop helper", cancellationToken: cancellationToken);
    }

    private async Task RestartProcessForRuleAsync(string processName, string sourceProcessName, string ruleId, RuleActionType actionType, CancellationToken cancellationToken)
    {
        var snapshot = await RestartProcessAsync(processName, $"rule {ruleId} restart", cancellationToken);
        if (snapshot is null)
        {
            RecordRuleFailure(sourceProcessName, ruleId, actionType, "Rule referenced an unknown process.", processName);
        }
    }

    private async Task StopProcessForRuleAsync(string processName, string sourceProcessName, string ruleId, RuleActionType actionType, CancellationToken cancellationToken)
    {
        var snapshot = await StopProcessAsync(processName, $"rule {ruleId} kill", cancellationToken);
        if (snapshot is null)
        {
            RecordRuleFailure(sourceProcessName, ruleId, actionType, "Rule referenced an unknown process.", processName);
        }
    }

    private async Task SetPriorityForRuleAsync(string processName, ManagedProcessPriority priority, string sourceProcessName, string ruleId, RuleActionType actionType, CancellationToken cancellationToken)
    {
        var snapshot = await SetPriorityAsync(processName, priority, $"rule {ruleId} set priority", cancellationToken);
        if (snapshot is null)
        {
            RecordRuleFailure(sourceProcessName, ruleId, actionType, "Rule referenced an unknown process.", processName);
        }
    }

    private bool TryGetHelperAgent(string? helperName, string sourceProcessName, string ruleId, RuleActionType actionType, out ManagedProcessAgent helperAgent)
    {
        helperAgent = null!;

        if (string.IsNullOrWhiteSpace(helperName))
        {
            RecordRuleFailure(sourceProcessName, ruleId, actionType, "Rule did not specify a helper target.");
            return false;
        }

        if (!_agents.TryGetValue(helperName, out var candidate) || !candidate.Options.IsHelper)
        {
            RecordRuleFailure(sourceProcessName, ruleId, actionType, "Rule referenced an unknown helper.", helperName);
            return false;
        }

        helperAgent = candidate;
        return true;
    }

    private void RecordRuleFailure(string processName, string ruleId, RuleActionType actionType, string message, string? target = null)
    {
        RecordEvent("rule.action.failed", processName, "rule", $"Rule {ruleId} action {actionType} failed. {message}",
            new Dictionary<string, string?>
            {
                ["ruleId"] = ruleId,
                ["action"] = actionType.ToString(),
                ["target"] = target,
                ["error"] = message
            });
    }

    private static LogLevel GetLogLevel(SupervisorEvent supervisorEvent)
    {
        if (string.Equals(supervisorEvent.EventType, "log.line", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        if (supervisorEvent.EventType.Contains(".failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(supervisorEvent.EventType, "external-log.missing", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        return LogLevel.Information;
    }
}
