using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DeerHunter.Configuration;
using DeerHunter.Models;

namespace DeerHunter.Services;

public sealed class ManagedProcessAgent
{
    private readonly SupervisorCoordinator _coordinator;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ManagedProcessAgent> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<ObservedLine> _observedLines = Channel.CreateUnbounded<ObservedLine>();
    private readonly List<ExternalLogTailer> _tailers = [];
    private readonly Dictionary<string, DateTimeOffset> _ruleCooldowns = new(StringComparer.OrdinalIgnoreCase);

    private Process? _process;
    private Task? _observedLineTask;
    private CancellationTokenSource? _runtimeCts;
    private bool _expectedExit;
    private int _restartAttempts;
    private int? _lastExitCode;
    private string? _lastError;
    private DateTimeOffset? _lastStartedAtUtc;
    private DateTimeOffset? _lastStoppedAtUtc;
    private ManagedProcessLifecycle _lifecycle = ManagedProcessLifecycle.Stopped;
    private ManagedProcessCondition _condition = ManagedProcessCondition.Unknown;
    private ManagedProcessPriority _priority;

    public ManagedProcessAgent(
        ManagedProcessOptions options,
        SupervisorCoordinator coordinator,
        IHostEnvironment environment,
        ILogger<ManagedProcessAgent> logger)
    {
        Options = options;
        _coordinator = coordinator;
        _environment = environment;
        _logger = logger;
        _priority = options.Priority;
        PublishSnapshot();
    }

    public ManagedProcessOptions Options { get; }

    public void Initialize()
    {
        _runtimeCts = new CancellationTokenSource();
        _observedLineTask = Task.Run(() => ProcessObservedLinesAsync(_runtimeCts.Token));

        foreach (var externalLog in Options.ExternalLogs)
        {
            var tailer = new ExternalLogTailer(
                externalLog,
                _coordinator.ResolvePath(externalLog.Path),
                observedLine => _observedLines.Writer.TryWrite(observedLine),
                _coordinator,
                Options.Name,
                _logger);

            _tailers.Add(tailer);
            tailer.Start(_runtimeCts.Token);
        }
    }

    public async Task<ManagedProcessSnapshot> StartAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return Snapshot();
            }

            _expectedExit = false;
            SetLifecycleNoLock(ManagedProcessLifecycle.Starting, reason);
            SetConditionNoLock(ManagedProcessCondition.Healthy, reason);
            _lastError = null;
            PublishSnapshot();

            _coordinator.RecordEvent("process.starting", Options.Name, "supervisor", $"Starting process {Options.Name}.",
                new Dictionary<string, string?> { ["reason"] = reason, ["command"] = Options.Command });

            try
            {
                var startInfo = BuildStartInfo();
                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (!string.IsNullOrEmpty(eventArgs.Data))
                    {
                        _observedLines.Writer.TryWrite(new ObservedLine("stdout", eventArgs.Data));
                    }
                };

                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (!string.IsNullOrEmpty(eventArgs.Data))
                    {
                        _observedLines.Writer.TryWrite(new ObservedLine("stderr", eventArgs.Data));
                    }
                };

                process.Exited += (_, _) => _ = HandleExitedAsync(process);

                if (!process.Start())
                {
                    throw new InvalidOperationException($"Process '{Options.Name}' failed to start.");
                }

                _process = process;
                _lastStartedAtUtc = DateTimeOffset.UtcNow;
                SetLifecycleNoLock(ManagedProcessLifecycle.Running, reason);
                SetConditionNoLock(ManagedProcessCondition.Healthy, reason);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                ApplyPriorityNoLock(_priority);

                _coordinator.RecordEvent("process.started", Options.Name, "supervisor", $"Started process {Options.Name}.",
                    new Dictionary<string, string?> { ["pid"] = process.Id.ToString(), ["reason"] = reason });

                PublishSnapshot();
            }
            catch (Exception exception)
            {
                _process = null;
                SetLifecycleNoLock(ManagedProcessLifecycle.Failed, reason);
                SetConditionNoLock(ManagedProcessCondition.Blocked, reason);
                _lastError = exception.Message;
                PublishSnapshot();

                _coordinator.RecordEvent("process.start.failed", Options.Name, "supervisor", $"Failed to start process {Options.Name}.",
                    new Dictionary<string, string?> { ["reason"] = reason, ["error"] = exception.Message });
            }

            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedProcessSnapshot> StopAsync(string reason, bool force = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is null || _process.HasExited)
            {
                SetLifecycleNoLock(ManagedProcessLifecycle.Stopped, reason);
                PublishSnapshot();
                return Snapshot();
            }

            _expectedExit = true;
            SetLifecycleNoLock(ManagedProcessLifecycle.Stopping, reason);
            PublishSnapshot();

            _coordinator.RecordEvent("process.stopping", Options.Name, "supervisor", $"Stopping process {Options.Name}.",
                new Dictionary<string, string?> { ["reason"] = reason });

            try
            {
                if (force || !_process.CloseMainWindow())
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _coordinator.RecordEvent("process.stop.failed", Options.Name, "supervisor", $"Failed to stop process {Options.Name}.",
                    new Dictionary<string, string?> { ["error"] = exception.Message, ["reason"] = reason });
            }

            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedProcessSnapshot> RestartAsync(string reason, CancellationToken cancellationToken)
    {
        _coordinator.RecordEvent("process.restarting", Options.Name, "supervisor", $"Restarting process {Options.Name}.",
            new Dictionary<string, string?> { ["reason"] = reason });

        SetLifecycleNoLock(ManagedProcessLifecycle.Restarting, reason);
        PublishSnapshot();

        await StopAsync(reason, cancellationToken: cancellationToken);
        return await StartAsync(reason, cancellationToken);
    }

    public async Task<ManagedProcessSnapshot> SetPriorityAsync(ManagedProcessPriority priority, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _priority = priority;
            ApplyPriorityNoLock(priority);
            PublishSnapshot();

            _coordinator.RecordEvent("process.priority.changed", Options.Name, "supervisor", $"Priority changed for {Options.Name}.",
                new Dictionary<string, string?> { ["priority"] = priority.ToString(), ["reason"] = reason });

            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetCondition(ManagedProcessCondition condition, string reason)
    {
        SetConditionNoLock(condition, reason);
        PublishSnapshot();

        _coordinator.RecordEvent("process.condition.changed", Options.Name, "rule", $"Condition changed for {Options.Name}.",
            new Dictionary<string, string?> { ["condition"] = condition.ToString(), ["reason"] = reason });
    }

    public async Task DisposeAsync(CancellationToken cancellationToken)
    {
        _runtimeCts?.Cancel();
        _observedLines.Writer.TryComplete();

        foreach (var tailer in _tailers)
        {
            await tailer.DisposeAsync();
        }

        await StopAsync("host shutdown", cancellationToken: cancellationToken);

        if (_observedLineTask is not null)
        {
            try
            {
                await _observedLineTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _runtimeCts?.Dispose();
        _gate.Dispose();
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var workingDirectory = string.IsNullOrWhiteSpace(Options.WorkingDirectory)
            ? _environment.ContentRootPath
            : _coordinator.ResolvePath(Options.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = Options.Command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in Options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var item in Options.Environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        return startInfo;
    }

    private async Task HandleExitedAsync(Process process)
    {
        await _gate.WaitAsync();
        try
        {
            _lastExitCode = SafeGetExitCode(process);
            _lastStoppedAtUtc = DateTimeOffset.UtcNow;
            _process = null;
            SetLifecycleNoLock(ManagedProcessLifecycle.Stopped, _expectedExit ? "expected exit" : "unexpected exit");
            PublishSnapshot();

            _coordinator.RecordEvent("process.exited", Options.Name, "supervisor", $"Process {Options.Name} exited.",
                new Dictionary<string, string?>
                {
                    ["exitCode"] = _lastExitCode?.ToString(),
                    ["expected"] = _expectedExit.ToString()
                });

            if (_expectedExit || !Options.Restart.Enabled)
            {
                _expectedExit = false;
                return;
            }

            if (Options.Restart.MaxAttempts > 0 && _restartAttempts >= Options.Restart.MaxAttempts)
            {
                SetLifecycleNoLock(ManagedProcessLifecycle.Failed, "restart attempts exhausted");
                SetConditionNoLock(ManagedProcessCondition.Blocked, "restart attempts exhausted");
                _lastError = "Restart attempts exhausted.";
                PublishSnapshot();

                _coordinator.RecordEvent("process.restart.exhausted", Options.Name, "supervisor", $"Restart attempts exhausted for {Options.Name}.",
                    new Dictionary<string, string?> { ["attempts"] = _restartAttempts.ToString() });
                return;
            }

            _restartAttempts++;
            SetLifecycleNoLock(ManagedProcessLifecycle.Backoff, "restart scheduled");
            PublishSnapshot();

            _coordinator.RecordEvent("process.restart.scheduled", Options.Name, "supervisor", $"Scheduling restart for {Options.Name}.",
                new Dictionary<string, string?> { ["delaySeconds"] = Options.Restart.DelaySeconds.ToString(), ["attempt"] = _restartAttempts.ToString() });
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Options.Restart.DelaySeconds), _runtimeCts?.Token ?? CancellationToken.None);
            await StartAsync("automatic restart", _runtimeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessObservedLinesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var observed in _observedLines.Reader.ReadAllAsync(cancellationToken))
            {
                _coordinator.RecordEvent("log.line", Options.Name, observed.Source, observed.Text,
                    new Dictionary<string, string?>
                    {
                        ["sourceType"] = observed.Source,
                        ["sourceName"] = observed.ExternalLogName ?? observed.Source,
                        ["externalLogName"] = observed.ExternalLogName
                    });

                foreach (var rule in Options.Rules)
                {
                    if (!MatchesSource(rule, observed) || !MatchesPattern(rule, observed.Text))
                    {
                        continue;
                    }

                    if (rule.CooldownSeconds > 0 &&
                        _ruleCooldowns.TryGetValue(rule.Id, out var lastTriggered) &&
                        DateTimeOffset.UtcNow - lastTriggered < TimeSpan.FromSeconds(rule.CooldownSeconds))
                    {
                        continue;
                    }

                    _ruleCooldowns[rule.Id] = DateTimeOffset.UtcNow;

                    _coordinator.RecordEvent("rule.matched", Options.Name, observed.Source, $"Rule {rule.Id} matched for {Options.Name}.",
                        new Dictionary<string, string?>
                        {
                            ["ruleId"] = rule.Id,
                            ["matchedProcess"] = Options.Name,
                            ["sourceType"] = observed.Source,
                            ["sourceName"] = observed.ExternalLogName ?? observed.Source,
                            ["externalLogName"] = observed.ExternalLogName,
                            ["line"] = observed.Text
                        });

                    await _coordinator.ExecuteRuleActionsAsync(Options.Name, rule, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool MatchesSource(SignalRuleOptions rule, ObservedLine observed)
    {
        return rule.Source switch
        {
            LogSourceType.Any => true,
            LogSourceType.Stdout => string.Equals(observed.Source, "stdout", StringComparison.OrdinalIgnoreCase),
            LogSourceType.Stderr => string.Equals(observed.Source, "stderr", StringComparison.OrdinalIgnoreCase),
            LogSourceType.ExternalLog => string.Equals(observed.Source, "external", StringComparison.OrdinalIgnoreCase) &&
                                         (string.IsNullOrWhiteSpace(rule.ExternalLogName) ||
                                          string.Equals(rule.ExternalLogName, observed.ExternalLogName, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static bool MatchesPattern(SignalRuleOptions rule, string line)
    {
        return rule.MatchType switch
        {
            LogMatchType.Contains => line.Contains(rule.Pattern, rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
            LogMatchType.Regex => Regex.IsMatch(line, rule.Pattern, rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
            _ => false
        };
    }

    private void ApplyPriorityNoLock(ManagedProcessPriority priority)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            _process.PriorityClass = priority switch
            {
                ManagedProcessPriority.Idle => ProcessPriorityClass.Idle,
                ManagedProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
                ManagedProcessPriority.Normal => ProcessPriorityClass.Normal,
                ManagedProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                ManagedProcessPriority.High => ProcessPriorityClass.High,
                ManagedProcessPriority.RealTime => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to set priority for {ProcessName}", Options.Name);
            _lastError = exception.Message;
        }
    }

    private ManagedProcessSnapshot Snapshot() => new(
        Name: Options.Name,
        Description: Options.Description,
        IsHelper: Options.IsHelper,
        HelperFor: Options.HelperFor,
        AutoStart: Options.AutoStart,
        Lifecycle: _lifecycle,
        Condition: _condition,
        Priority: _priority,
        ProcessId: _process is { HasExited: false } ? _process.Id : null,
        RestartAttempts: _restartAttempts,
        LastExitCode: _lastExitCode,
        LastStartedAtUtc: _lastStartedAtUtc,
        LastStoppedAtUtc: _lastStoppedAtUtc,
        LastError: _lastError);

    private void PublishSnapshot() => _coordinator.UpdateSnapshot(Snapshot());

    private void SetLifecycleNoLock(ManagedProcessLifecycle lifecycle, string reason)
    {
        if (_lifecycle == lifecycle)
        {
            return;
        }

        var previous = _lifecycle;
        _lifecycle = lifecycle;
        _logger.LogInformation("Process {ProcessName} lifecycle changed from {PreviousLifecycle} to {CurrentLifecycle}. Reason: {Reason}",
            Options.Name,
            previous,
            lifecycle,
            reason);
    }

    private void SetConditionNoLock(ManagedProcessCondition condition, string reason)
    {
        if (_condition == condition)
        {
            return;
        }

        var previous = _condition;
        _condition = condition;
        _logger.LogInformation("Process {ProcessName} condition changed from {PreviousCondition} to {CurrentCondition}. Reason: {Reason}",
            Options.Name,
            previous,
            condition,
            reason);
    }

    private static int? SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }
}
