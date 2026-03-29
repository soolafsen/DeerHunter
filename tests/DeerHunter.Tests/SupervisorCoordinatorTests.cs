using DeerHunter.Configuration;
using DeerHunter.Models;
using DeerHunter.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeerHunter.Tests;

public sealed class SupervisorCoordinatorTests : IAsyncLifetime
{
    private readonly string _workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private readonly List<SupervisorCoordinator> _coordinators = [];

    [Fact]
    public async Task StartAsync_StartsTwoConfiguredProcesses_AndTracksRunningSnapshots()
    {
        var coordinator = CreateCoordinator(
        [
            CreateLongRunningProcess("alpha"),
            CreateLongRunningProcess("beta")
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var snapshots = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshots();
                return current.Count == 2 && current.All(static snapshot => snapshot.Lifecycle == ManagedProcessLifecycle.Running)
                    ? current
                    : null;
            });

        Assert.Equal(["alpha", "beta"], snapshots.Select(static snapshot => snapshot.Name).ToArray());
    }

    [Fact]
    public async Task UnexpectedExit_RecordsExitAndSchedulesRestart()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "flaky",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "restart-me", "--delay-ms", "50", "--exit-code", "5"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = true,
                    DelaySeconds = 0,
                    MaxAttempts = 1
                }
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        await WaitForConditionAsync(
            () => coordinator.GetRecentEvents(20).Any(static @event => @event.EventType == "process.exited" && @event.ProcessName == "flaky"));

        var restartScheduled = await WaitForAsync(
            () => coordinator.GetRecentEvents(20).FirstOrDefault(static @event => @event.EventType == "process.restart.scheduled" && @event.ProcessName == "flaky"));

        Assert.Equal("0", restartScheduled.Details["delaySeconds"]);

        var failedSnapshot = await WaitForAsync(
            () =>
            {
                var snapshot = coordinator.GetSnapshot("flaky");
                return snapshot is { Lifecycle: ManagedProcessLifecycle.Failed, RestartAttempts: 1, LastExitCode: 5 }
                    ? snapshot
                    : null;
            });

        Assert.Equal("Restart attempts exhausted.", failedSnapshot.LastError);
    }

    [Fact]
    public async Task OutputEvents_AreTimestampedAndAttributedToTheCorrectProcess()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "hello-out", "--stderr", "hello-err", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var events = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetRecentEvents(20)
                    .Where(static @event => @event.EventType == "log.line" && @event.ProcessName == "speaker")
                    .ToArray();

                return current.Any(static @event => @event.Source == "stdout" && @event.Message == "hello-out") &&
                       current.Any(static @event => @event.Source == "stderr" && @event.Message == "hello-err")
                    ? current
                    : null;
            });

        Assert.All(events, static @event => Assert.True(@event.TimestampUtc > DateTimeOffset.MinValue));
        Assert.Contains(events, static @event => @event.Source == "stdout" && @event.Message == "hello-out");
        Assert.Contains(events, static @event => @event.Source == "stderr" && @event.Message == "hello-err");
    }

    [Fact]
    public async Task InvalidExecutable_RecordsFailedStart_AndContinuesSupervisingOtherProcesses()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "broken",
                Command = "C:\\definitely-missing\\deerhunter-missing.exe",
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            },
            CreateLongRunningProcess("healthy")
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var brokenSnapshot = await WaitForAsync(
            () =>
            {
                var snapshot = coordinator.GetSnapshot("broken");
                return snapshot is { Lifecycle: ManagedProcessLifecycle.Failed } ? snapshot : null;
            });

        var healthySnapshot = await WaitForAsync(
            () =>
            {
                var snapshot = coordinator.GetSnapshot("healthy");
                return snapshot is { Lifecycle: ManagedProcessLifecycle.Running } ? snapshot : null;
            });

        var failedStart = coordinator.GetRecentEvents(20)
            .Single(@event => @event.EventType == "process.start.failed" && @event.ProcessName == "broken");

        Assert.Contains("error", failedStart.Details.Keys);
        Assert.Equal(ManagedProcessLifecycle.Failed, brokenSnapshot.Lifecycle);
        Assert.Equal(ManagedProcessLifecycle.Running, healthySnapshot.Lifecycle);
    }

    [Fact]
    public async Task ExternalLogLines_AppearInTheUnifiedEventStream_WithSourceMetadata()
    {
        var tempDirectory = CreateTempDirectory();
        var externalLogPath = Path.Combine(tempDirectory, "service.log");
        await File.WriteAllTextAsync(externalLogPath, "seed-line\r\n");

        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "hello-out", "--stderr", "hello-err", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                ExternalLogs =
                [
                    new ExternalLogOptions
                    {
                        Name = "service-log",
                        Path = externalLogPath,
                        StartPosition = ExternalLogStartPosition.Beginning
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);
        await File.AppendAllTextAsync(externalLogPath, "tail-line\r\n");

        var events = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetRecentEvents(30)
                    .Where(static @event => @event.EventType == "log.line" && @event.ProcessName == "speaker")
                    .ToArray();

                return current.Any(static @event => @event.Source == "stdout" && @event.Message == "hello-out") &&
                       current.Any(static @event => @event.Source == "stderr" && @event.Message == "hello-err") &&
                       current.Any(static @event => @event.Source == "external" && @event.Message == "seed-line") &&
                       current.Any(static @event => @event.Source == "external" && @event.Message == "tail-line")
                    ? current
                    : null;
            });

        var externalEvent = Assert.Single(events.Where(static @event => @event.Source == "external" && @event.Message == "tail-line"));
        Assert.Equal("external", externalEvent.Details["sourceType"]);
        Assert.Equal("service-log", externalEvent.Details["sourceName"]);
        Assert.Equal("service-log", externalEvent.Details["externalLogName"]);
    }

    [Fact]
    public async Task MissingExternalLog_RecordsWarning_AndSupervisorKeepsRunning()
    {
        var tempDirectory = CreateTempDirectory();
        var missingLogPath = Path.Combine(tempDirectory, "missing.log");

        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                ExternalLogs =
                [
                    new ExternalLogOptions
                    {
                        Name = "optional-log",
                        Path = missingLogPath
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var missingEvent = await WaitForAsync(
            () => coordinator.GetRecentEvents(20)
                .FirstOrDefault(static @event => @event.EventType == "external-log.missing" && @event.ProcessName == "speaker"));

        var snapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("speaker");
                return current is { Lifecycle: ManagedProcessLifecycle.Running } ? current : null;
            });

        Assert.Equal("optional-log", missingEvent.Details["externalLogName"]);
        Assert.Equal(missingLogPath, missingEvent.Details["path"]);
        Assert.Equal(ManagedProcessLifecycle.Running, snapshot.Lifecycle);
    }

    [Fact]
    public async Task RecreatedExternalLog_IsRetainedWithoutReplayingSkippedHistory()
    {
        var tempDirectory = CreateTempDirectory();
        var externalLogPath = Path.Combine(tempDirectory, "rotating.log");
        await File.WriteAllTextAsync(externalLogPath, "old-skip\r\n");

        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                ExternalLogs =
                [
                    new ExternalLogOptions
                    {
                        Name = "rotating-log",
                        Path = externalLogPath,
                        StartPosition = ExternalLogStartPosition.End
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);
        await Task.Delay(700);
        await File.AppendAllTextAsync(externalLogPath, "line-one\r\n");

        await WaitForConditionAsync(
            () => coordinator.GetRecentEvents(20)
                .Any(static @event => @event.EventType == "log.line" && @event.Source == "external" && @event.Message == "line-one"));

        await Task.Delay(1100);
        File.Delete(externalLogPath);
        await File.WriteAllTextAsync(externalLogPath, "line-two\r\n");

        var rotatedEvent = await WaitForAsync(
            () => coordinator.GetRecentEvents(30)
                .FirstOrDefault(static @event => @event.EventType == "external-log.rotated" && @event.ProcessName == "speaker"));

        var lineTwoEvent = await WaitForAsync(
            () => coordinator.GetRecentEvents(30)
                .FirstOrDefault(static @event => @event.EventType == "log.line" && @event.Source == "external" && @event.Message == "line-two"));

        var logEvents = coordinator.GetRecentEvents(30)
            .Where(static @event => @event.EventType == "log.line" && @event.Source == "external")
            .Select(static @event => @event.Message)
            .ToArray();

        Assert.Equal("rotating-log", rotatedEvent.Details["externalLogName"]);
        Assert.Equal("line-two", lineTwoEvent.Message);
        Assert.DoesNotContain("old-skip", logEvents);
        Assert.Equal(1, logEvents.Count(static message => message == "line-one"));
        Assert.Equal(1, logEvents.Count(static message => message == "line-two"));
    }

    [Fact]
    public async Task RegexRuleMatch_RecordsSignalMetadata_AndChangesCondition()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ALERT 42", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                Rules =
                [
                    new SignalRuleOptions
                    {
                        Id = "alert-regex",
                        MatchType = LogMatchType.Regex,
                        Pattern = "^ALERT \\d+$",
                        Source = LogSourceType.Stdout,
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.SetCondition,
                                Condition = ManagedProcessCondition.Degraded
                            }
                        ]
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var matchedEvent = await WaitForAsync(
            () => coordinator.GetRecentEvents(20)
                .FirstOrDefault(static @event => @event.EventType == "rule.matched" && @event.ProcessName == "speaker"));

        var snapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("speaker");
                return current is { Condition: ManagedProcessCondition.Degraded } ? current : null;
            });

        Assert.Equal("stdout", matchedEvent.Source);
        Assert.Equal("alert-regex", matchedEvent.Details["ruleId"]);
        Assert.Equal("speaker", matchedEvent.Details["matchedProcess"]);
        Assert.Equal("stdout", matchedEvent.Details["sourceType"]);
        Assert.Equal("stdout", matchedEvent.Details["sourceName"]);
        Assert.Equal("ALERT 42", matchedEvent.Details["line"]);
        Assert.Equal(ManagedProcessCondition.Degraded, snapshot.Condition);
    }

    [Fact]
    public async Task RestartRule_StopsAndRestartsTarget_AndRecordsTriggerReason()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "restart-me", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                Rules =
                [
                    new SignalRuleOptions
                    {
                        Id = "restart-rule",
                        Pattern = "restart-me",
                        Source = LogSourceType.Stdout,
                        CooldownSeconds = 60,
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.Restart
                            }
                        ]
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var restartingEvent = await WaitForAsync(
            () => coordinator.GetRecentEvents(30)
                .FirstOrDefault(static @event => @event.EventType == "process.restarting" && @event.ProcessName == "speaker"));

        await WaitForConditionAsync(
            () => coordinator.GetRecentEvents(40)
                .Count(static @event => @event.EventType == "process.started" && @event.ProcessName == "speaker") >= 2);

        var snapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("speaker");
                return current is { Lifecycle: ManagedProcessLifecycle.Running } ? current : null;
            });

        Assert.Equal("rule restart-rule restart", restartingEvent.Details["reason"]);
        Assert.Equal(ManagedProcessLifecycle.Running, snapshot.Lifecycle);
    }

    [Fact]
    public async Task PausedSupervision_SkipsRuleActions_AndAutomaticRestart()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "restart-me", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                Rules =
                [
                    new SignalRuleOptions
                    {
                        Id = "restart-rule",
                        Pattern = "restart-me",
                        Source = LogSourceType.Stdout,
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.Restart
                            }
                        ]
                    }
                ]
            },
            new ManagedProcessOptions
            {
                Name = "flaky",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "exit-now", "--delay-ms", "50", "--exit-code", "5"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = true,
                    DelaySeconds = 0,
                    MaxAttempts = 1
                }
            }
        ]);

        coordinator.PauseSupervision("test", "pause before runtime checks");
        await coordinator.StartAsync(CancellationToken.None);

        var skippedRuleAction = await WaitForAsync(
            () => coordinator.GetRecentEvents(30)
                .FirstOrDefault(static @event => @event.EventType == "rule.action.skipped" && @event.ProcessName == "speaker"));

        var skippedRestart = await WaitForAsync(
            () => coordinator.GetRecentEvents(30)
                .FirstOrDefault(static @event => @event.EventType == "process.restart.skipped" && @event.ProcessName == "flaky"));

        var flakySnapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("flaky");
                return current is { Lifecycle: ManagedProcessLifecycle.Stopped, LastExitCode: 5 } ? current : null;
            });

        var speakerSnapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("speaker");
                return current is { Lifecycle: ManagedProcessLifecycle.Running } ? current : null;
            });

        await Task.Delay(250);

        Assert.Equal("restart-rule", skippedRuleAction.Details["ruleId"]);
        Assert.Equal("supervision paused", skippedRuleAction.Details["reason"]);
        Assert.Equal("supervision paused", skippedRestart.Details["reason"]);
        Assert.Equal(ManagedProcessLifecycle.Stopped, flakySnapshot.Lifecycle);
        Assert.Equal(5, flakySnapshot.LastExitCode);
        Assert.Equal(ManagedProcessLifecycle.Running, speakerSnapshot.Lifecycle);
        Assert.DoesNotContain(
            coordinator.GetRecentEvents(40),
            static @event => @event.EventType == "process.restarting" && @event.ProcessName == "speaker");
        Assert.DoesNotContain(
            coordinator.GetRecentEvents(40),
            static @event => @event.EventType == "process.restart.scheduled" && @event.ProcessName == "flaky");
    }

    [Fact]
    public async Task HelperRules_StartAndStopDefinedHelperOnly()
    {
        var tempDirectory = CreateTempDirectory();
        var externalLogPath = Path.Combine(tempDirectory, "helper-control.log");
        await File.WriteAllTextAsync(externalLogPath, string.Empty);

        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "main",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "start-helper", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                ExternalLogs =
                [
                    new ExternalLogOptions
                    {
                        Name = "helper-control",
                        Path = externalLogPath,
                        StartPosition = ExternalLogStartPosition.End
                    }
                ],
                Rules =
                [
                    new SignalRuleOptions
                    {
                        Id = "start-helper-rule",
                        Pattern = "start-helper",
                        Source = LogSourceType.Stdout,
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.StartHelper,
                                TargetProcess = "helper"
                            }
                        ]
                    },
                    new SignalRuleOptions
                    {
                        Id = "stop-helper-rule",
                        Pattern = "stop-helper",
                        Source = LogSourceType.ExternalLog,
                        ExternalLogName = "helper-control",
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.StopHelper,
                                TargetProcess = "helper"
                            }
                        ]
                    }
                ]
            },
            CreateHelperProcess("helper")
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var startedHelper = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("helper");
                return current is { Lifecycle: ManagedProcessLifecycle.Running } ? current : null;
            });

        await Task.Delay(700);
        await File.AppendAllTextAsync(externalLogPath, "stop-helper\r\n");

        var stoppedHelper = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("helper");
                return current is { Lifecycle: ManagedProcessLifecycle.Stopped } ? current : null;
            });

        Assert.Equal(ManagedProcessLifecycle.Running, startedHelper.Lifecycle);
        Assert.Equal(ManagedProcessLifecycle.Stopped, stoppedHelper.Lifecycle);
        Assert.DoesNotContain(
            coordinator.GetRecentEvents(40),
            static @event => @event.EventType == "rule.action.failed" && @event.ProcessName == "main");
    }

    [Fact]
    public async Task UnknownRuleTargets_RecordExecutionFailures_AndSupervisorKeepsRunning()
    {
        var coordinator = CreateCoordinator(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "trigger", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                },
                Rules =
                [
                    new SignalRuleOptions
                    {
                        Id = "bad-targets",
                        Pattern = "trigger",
                        Source = LogSourceType.Stdout,
                        Actions =
                        [
                            new RuleActionOptions
                            {
                                Type = RuleActionType.Restart,
                                TargetProcess = "missing-process"
                            },
                            new RuleActionOptions
                            {
                                Type = RuleActionType.StartHelper,
                                TargetProcess = "missing-helper"
                            }
                        ]
                    }
                ]
            }
        ]);

        await coordinator.StartAsync(CancellationToken.None);

        var failures = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetRecentEvents(20)
                    .Where(static @event => @event.EventType == "rule.action.failed" && @event.ProcessName == "speaker")
                    .ToArray();

                return current.Length >= 2 ? current : null;
            });

        var snapshot = await WaitForAsync(
            () =>
            {
                var current = coordinator.GetSnapshot("speaker");
                return current is { Lifecycle: ManagedProcessLifecycle.Running } ? current : null;
            });

        Assert.Contains(failures, static @event => @event.Details["target"] == "missing-process");
        Assert.Contains(failures, static @event => @event.Details["target"] == "missing-helper");
        Assert.Equal(ManagedProcessLifecycle.Running, snapshot.Lifecycle);
    }

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var coordinator in _coordinators)
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        _coordinators.Clear();
    }

    private string SampleProcessPath => Path.Combine(AppContext.BaseDirectory, "DeerHunter.SampleProcess.exe");

    private ManagedProcessOptions CreateLongRunningProcess(string name)
        => new()
        {
            Name = name,
            Command = SampleProcessPath,
            Arguments = ["--stdout", $"{name}-ready", "--wait"],
            Restart = new RestartPolicyOptions
            {
                Enabled = false
            }
        };

    private ManagedProcessOptions CreateHelperProcess(string name)
        => new()
        {
            Name = name,
            Command = SampleProcessPath,
            Arguments = ["--stdout", $"{name}-ready", "--wait"],
            AutoStart = false,
            IsHelper = true,
            Restart = new RestartPolicyOptions
            {
                Enabled = false
            }
        };

    private SupervisorCoordinator CreateCoordinator(IReadOnlyList<ManagedProcessOptions> processes)
    {
        var options = Options.Create(new DeerHunterOptions
        {
            EventBufferSize = 200,
            JournalPath = Path.Combine(Path.GetTempPath(), $"deerhunter-tests-{Guid.NewGuid():N}.jsonl"),
            Processes = processes.ToList()
        });

        var environment = new TestHostEnvironment(_workspaceRoot);
        var hostRuntimeState = new HostRuntimeState(Path.Combine(Path.GetTempPath(), $"deerhunter-config-{Guid.NewGuid():N}.json"));
        var eventStore = new EventStore(options);
        var eventJournal = new EventJournal(options, environment, NullLogger<EventJournal>.Instance);
        var coordinator = new SupervisorCoordinator(
            options,
            eventStore,
            eventJournal,
            environment,
            hostRuntimeState,
            new TestApplicationLifetime(),
            NullLoggerFactory.Instance,
            NullLogger<SupervisorCoordinator>.Instance);

        _coordinators.Add(coordinator);
        return coordinator;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deerhunter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, int timeoutMilliseconds = 5000, int pollMilliseconds = 50)
        where T : class
    {
        var startedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - startedAt < TimeSpan.FromMilliseconds(timeoutMilliseconds))
        {
            var result = probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(pollMilliseconds);
        }

        throw new TimeoutException("Condition was not met before the timeout expired.");
    }

    private static async Task WaitForConditionAsync(Func<bool> probe, int timeoutMilliseconds = 5000, int pollMilliseconds = 50)
    {
        var startedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - startedAt < TimeSpan.FromMilliseconds(timeoutMilliseconds))
        {
            if (probe())
            {
                return;
            }

            await Task.Delay(pollMilliseconds);
        }

        throw new TimeoutException("Condition was not met before the timeout expired.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "DeerHunter.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
