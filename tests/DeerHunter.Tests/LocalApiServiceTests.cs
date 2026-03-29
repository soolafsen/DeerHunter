using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeerHunter.Configuration;
using DeerHunter.Models;
using DeerHunter.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeerHunter.Tests;

public sealed class LocalApiServiceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private readonly List<SupervisorCoordinator> _coordinators = [];
    private readonly List<LocalApiService> _apiServices = [];

    [Fact]
    public async Task ProcessesAndEventsEndpoints_ReturnSnapshotsAndBoundedEventHistory()
    {
        using var client = await CreateClientAsync(
        [
            new ManagedProcessOptions
            {
                Name = "speaker",
                Description = "Speaker process",
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

        var processesResponse = await client.GetAsync("/api/processes");
        processesResponse.EnsureSuccessStatusCode();
        var processes = await processesResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var process = Assert.Single(processes.EnumerateArray());
        Assert.Equal("speaker", process.GetProperty("id").GetString());
        Assert.Equal("Speaker process", process.GetProperty("name").GetString());
        Assert.Equal("speaker", process.GetProperty("snapshot").GetProperty("name").GetString());

        var events = await WaitForEventsAsync(
            client,
            events => events.Any(static item => item.GetProperty("eventType").GetString() == "process.started") &&
                      events.Any(static item => item.GetProperty("eventType").GetString() == "log.line") &&
                      events.Any(static item => item.GetProperty("eventType").GetString() == "rule.matched") &&
                      events.Any(static item => item.GetProperty("eventType").GetString() == "process.restarting"));

        Assert.Contains(events, static item => item.GetProperty("eventType").GetString() == "process.started");
        Assert.Contains(events, static item => item.GetProperty("eventType").GetString() == "log.line");
        Assert.Contains(events, static item => item.GetProperty("eventType").GetString() == "rule.matched");
        Assert.Contains(events, static item => item.GetProperty("eventType").GetString() == "process.restarting");
    }

    [Fact]
    public async Task RestartAction_RestartsKnownProcess_AndUnknownProcessReturnsClientError()
    {
        using var client = await CreateClientAsync(
        [
            new ManagedProcessOptions
            {
                Name = "manual",
                Description = "Manual process",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            }
        ]);

        var restartResponse = await client.PostAsync("/api/processes/manual/actions/restart", content: null);
        restartResponse.EnsureSuccessStatusCode();

        var events = await WaitForEventsAsync(
            client,
            events => events.Any(static item => item.GetProperty("eventType").GetString() == "process.restarting" &&
                                                item.GetProperty("processName").GetString() == "manual" &&
                                                item.GetProperty("details").GetProperty("reason").GetString() == "manual api restart"));

        Assert.Contains(
            events,
            static item => item.GetProperty("eventType").GetString() == "process.restarting" &&
                           item.GetProperty("details").GetProperty("reason").GetString() == "manual api restart");

        var missingResponse = await client.PostAsync("/api/processes/missing/actions/restart", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        var error = await missingResponse.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("unknown_process", error.Code);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);

        var processesResponse = await client.GetAsync("/api/processes");
        processesResponse.EnsureSuccessStatusCode();
        var processes = await processesResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var process = Assert.Single(processes.EnumerateArray());
        Assert.Equal("Running", process.GetProperty("snapshot").GetProperty("lifecycle").GetString());
    }

    [Fact]
    public async Task PriorityAction_UpdatesSnapshot_AndInvalidPriorityReturnsClientError()
    {
        using var client = await CreateClientAsync(
        [
            new ManagedProcessOptions
            {
                Name = "manual",
                Description = "Manual process",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            }
        ]);

        var priorityResponse = await client.PostAsJsonAsync("/api/processes/manual/priority", new
        {
            priority = "High"
        });
        priorityResponse.EnsureSuccessStatusCode();

        var events = await WaitForEventsAsync(
            client,
            events => events.Any(static item => item.GetProperty("eventType").GetString() == "process.priority.changed" &&
                                                item.GetProperty("processName").GetString() == "manual" &&
                                                item.GetProperty("details").GetProperty("priority").GetString() == "High" &&
                                                item.GetProperty("details").GetProperty("reason").GetString() == "manual api priority"));

        Assert.Contains(
            events,
            static item => item.GetProperty("eventType").GetString() == "process.priority.changed" &&
                           item.GetProperty("details").GetProperty("priority").GetString() == "High" &&
                           item.GetProperty("details").GetProperty("reason").GetString() == "manual api priority");

        var processesResponse = await client.GetAsync("/api/processes");
        processesResponse.EnsureSuccessStatusCode();
        var processes = await processesResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var process = Assert.Single(processes.EnumerateArray());
        Assert.Equal("High", process.GetProperty("snapshot").GetProperty("priority").GetString());

        var invalidPriorityResponse = await client.PostAsJsonAsync("/api/processes/manual/priority", new
        {
            priority = "Turbo"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPriorityResponse.StatusCode);

        var invalidPriorityError = await invalidPriorityResponse.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(invalidPriorityError);
        Assert.Equal("invalid_priority", invalidPriorityError.Code);
    }

    [Fact]
    public async Task NoisyEventInput_RemainsResponsive_AndTrimsAccordingToRetentionRules()
    {
        const int capacity = 25;
        var fixture = await CreateApiFixtureAsync([], capacity);
        using var client = fixture.Client;

        for (var index = 0; index < 500; index++)
        {
            fixture.Coordinator.RecordEvent("log.line", "noisy", "stdout", $"line-{index}");
        }

        var response = await client.GetAsync("/api/events?take=500");
        response.EnsureSuccessStatusCode();
        var events = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).EnumerateArray().ToArray();

        Assert.Equal(capacity, events.Length);
        Assert.Equal("line-475", events[0].GetProperty("message").GetString());
        Assert.Equal("line-499", events[^1].GetProperty("message").GetString());
        Assert.Equal(capacity.ToString(), events[^1].GetProperty("details").GetProperty("retentionCapacity").GetString());
        Assert.Equal(capacity.ToString(), events[^1].GetProperty("details").GetProperty("retainedEvents").GetString());
        Assert.Equal("475", events[^1].GetProperty("details").GetProperty("droppedEvents").GetString());

        var processesResponse = await client.GetAsync("/api/processes");
        processesResponse.EnsureSuccessStatusCode();
        var processes = await processesResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Empty(processes.EnumerateArray());
    }

    [Fact]
    public async Task HostStatusAndActions_ReportHostState_AndPausePersistsUntilResume()
    {
        var fixture = await CreateApiFixtureAsync(
        [
            new ManagedProcessOptions
            {
                Name = "main",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            },
            new ManagedProcessOptions
            {
                Name = "helper",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "helper-ready", "--wait"],
                AutoStart = false,
                IsHelper = true,
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            }
        ], eventBufferSize: 200);

        using var client = fixture.Client;

        var initialStatusResponse = await client.GetAsync("/api/host/status");
        initialStatusResponse.EnsureSuccessStatusCode();
        var initialStatus = await initialStatusResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Running", initialStatus.GetProperty("supervisionState").GetString());
        Assert.False(initialStatus.GetProperty("supervisionPaused").GetBoolean());
        Assert.Equal(fixture.ConfigPath, initialStatus.GetProperty("configPath").GetString());
        Assert.Equal(1, initialStatus.GetProperty("managedProcessCount").GetInt32());
        Assert.Equal(1, initialStatus.GetProperty("helperCount").GetInt32());

        var pauseResponse = await client.PostAsync("/api/host/actions/pause", content: null);
        pauseResponse.EnsureSuccessStatusCode();

        var pausedStatusResponse = await client.GetAsync("/api/host/status");
        pausedStatusResponse.EnsureSuccessStatusCode();
        var pausedStatus = await pausedStatusResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Paused", pausedStatus.GetProperty("supervisionState").GetString());
        Assert.True(pausedStatus.GetProperty("supervisionPaused").GetBoolean());

        var pauseEvent = await WaitForEventsAsync(
            client,
            events => events.Any(static item => item.GetProperty("eventType").GetString() == "host.supervision.paused" &&
                                                item.GetProperty("source").GetString() == "host.api" &&
                                                item.GetProperty("details").GetProperty("reason").GetString() == "manual api pause supervision"));

        Assert.Contains(
            pauseEvent,
            static item => item.GetProperty("eventType").GetString() == "host.supervision.paused" &&
                           item.GetProperty("details").GetProperty("reason").GetString() == "manual api pause supervision");

        var resumeResponse = await client.PostAsync("/api/host/actions/resume", content: null);
        resumeResponse.EnsureSuccessStatusCode();

        var resumedStatusResponse = await client.GetAsync("/api/host/status");
        resumedStatusResponse.EnsureSuccessStatusCode();
        var resumedStatus = await resumedStatusResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Running", resumedStatus.GetProperty("supervisionState").GetString());
        Assert.False(resumedStatus.GetProperty("supervisionPaused").GetBoolean());
    }

    [Fact]
    public async Task HostActions_ReturnStructuredErrors_ForUnknownActionsAndWrongMethods()
    {
        var fixture = await CreateApiFixtureAsync([], eventBufferSize: 200);
        using var client = fixture.Client;

        var baselineStatusResponse = await client.GetAsync("/api/host/status");
        baselineStatusResponse.EnsureSuccessStatusCode();
        var baselineStatus = await baselineStatusResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var wrongMethodResponse = await client.GetAsync("/api/host/actions/pause");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethodResponse.StatusCode);
        var wrongMethodError = await wrongMethodResponse.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(wrongMethodError);
        Assert.Equal("method_not_allowed", wrongMethodError.Code);

        var unknownActionResponse = await client.PostAsync("/api/host/actions/unknown", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, unknownActionResponse.StatusCode);
        var unknownActionError = await unknownActionResponse.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(unknownActionError);
        Assert.Equal("unknown_host_action", unknownActionError.Code);

        var afterStatusResponse = await client.GetAsync("/api/host/status");
        afterStatusResponse.EnsureSuccessStatusCode();
        var afterStatus = await afterStatusResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(baselineStatus.GetProperty("supervisionState").GetString(), afterStatus.GetProperty("supervisionState").GetString());
        Assert.Equal(baselineStatus.GetProperty("supervisionPaused").GetBoolean(), afterStatus.GetProperty("supervisionPaused").GetBoolean());
    }

    [Fact]
    public async Task DashboardRootAndAssets_AreServed_FromLocalhostListener_AndMissingAssetsReturn404()
    {
        var fixture = await CreateApiFixtureAsync(
        [
            new ManagedProcessOptions
            {
                Name = "main",
                Description = "Main worker",
                Command = SampleProcessPath,
                Arguments = ["--stdout", "ready", "--wait"],
                Restart = new RestartPolicyOptions
                {
                    Enabled = false
                }
            }
        ], eventBufferSize: 200);

        using var client = fixture.Client;

        var rootResponse = await client.GetAsync("/");
        rootResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", rootResponse.Content.Headers.ContentType?.ToString());
        var html = await rootResponse.Content.ReadAsStringAsync();
        Assert.Contains("Host Summary", html, StringComparison.Ordinal);
        Assert.Contains("Processes", html, StringComparison.Ordinal);
        Assert.Contains("Events", html, StringComparison.Ordinal);
        Assert.Contains("Actions", html, StringComparison.Ordinal);

        var scriptResponse = await client.GetAsync("/dashboard.js");
        scriptResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/javascript; charset=utf-8", scriptResponse.Content.Headers.ContentType?.ToString());
        var script = await scriptResponse.Content.ReadAsStringAsync();
        Assert.Contains("loadDashboard", script, StringComparison.Ordinal);
        Assert.Contains("timestampUtc", script, StringComparison.Ordinal);
        Assert.Contains("/api/events?take=", script, StringComparison.Ordinal);
        Assert.Contains("Recent events could not be loaded. Try refresh again.", script, StringComparison.Ordinal);

        var missingAssetResponse = await client.GetAsync("/missing-dashboard-asset.js");
        Assert.Equal(HttpStatusCode.NotFound, missingAssetResponse.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", missingAssetResponse.Content.Headers.ContentType?.ToString());
        var missingMessage = await missingAssetResponse.Content.ReadAsStringAsync();
        Assert.Contains("Dashboard asset '/missing-dashboard-asset.js' was not found.", missingMessage, StringComparison.Ordinal);

        var statusResponse = await client.GetAsync("/api/host/status");
        statusResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/json; charset=utf-8", statusResponse.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task ReloadAndShutdownHostActions_ReturnSuccess_AndRecordAcceptedEvents()
    {
        var fixture = await CreateApiFixtureAsync([], eventBufferSize: 200);
        using var client = fixture.Client;

        var reloadResponse = await client.PostAsync("/api/host/actions/reload", content: null);
        reloadResponse.EnsureSuccessStatusCode();

        var shutdownResponse = await client.PostAsync("/api/host/actions/shutdown", content: null);
        shutdownResponse.EnsureSuccessStatusCode();

        var events = await WaitForEventsAsync(
            client,
            events => events.Any(static item => item.GetProperty("eventType").GetString() == "host.configuration.reload.requested") &&
                      events.Any(static item => item.GetProperty("eventType").GetString() == "host.shutdown.requested"));

        Assert.Contains(
            events,
            static item => item.GetProperty("eventType").GetString() == "host.configuration.reload.requested" &&
                           item.GetProperty("details").GetProperty("reason").GetString() == "manual api reload configuration");
        Assert.Contains(
            events,
            static item => item.GetProperty("eventType").GetString() == "host.shutdown.requested" &&
                           item.GetProperty("details").GetProperty("reason").GetString() == "manual api clean shutdown");
    }

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var apiService in _apiServices)
        {
            await apiService.StopAsync(CancellationToken.None);
        }

        foreach (var coordinator in _coordinators)
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        _apiServices.Clear();
        _coordinators.Clear();
    }

    private string SampleProcessPath => Path.Combine(AppContext.BaseDirectory, "DeerHunter.SampleProcess.exe");

    private async Task<HttpClient> CreateClientAsync(IReadOnlyList<ManagedProcessOptions> processes)
    {
        var fixture = await CreateApiFixtureAsync(processes, eventBufferSize: 200);
        return fixture.Client;
    }

    private async Task<ApiFixture> CreateApiFixtureAsync(IReadOnlyList<ManagedProcessOptions> processes, int eventBufferSize)
    {
        var port = GetAvailablePort();
        var apiUrl = $"http://127.0.0.1:{port}/";
        var configPath = Path.Combine(Path.GetTempPath(), $"deerhunter-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, "{ }");
        var options = Options.Create(new DeerHunterOptions
        {
            EventBufferSize = eventBufferSize,
            JournalPath = Path.Combine(Path.GetTempPath(), $"deerhunter-tests-{Guid.NewGuid():N}.jsonl"),
            Api = new ApiOptions
            {
                Urls = [apiUrl]
            },
            Processes = processes.ToList()
        });

        var environment = new TestHostEnvironment(_workspaceRoot);
        var hostRuntimeState = new HostRuntimeState(configPath);
        var applicationLifetime = new TestApplicationLifetime();
        var eventStore = new EventStore(options);
        var eventJournal = new EventJournal(options, environment, NullLogger<EventJournal>.Instance);
        var coordinator = new SupervisorCoordinator(
            options,
            eventStore,
            eventJournal,
            environment,
            hostRuntimeState,
            applicationLifetime,
            NullLoggerFactory.Instance,
            NullLogger<SupervisorCoordinator>.Instance);
        var apiService = new LocalApiService(options, coordinator, NullLogger<LocalApiService>.Instance);

        _coordinators.Add(coordinator);
        _apiServices.Add(apiService);

        await coordinator.StartAsync(CancellationToken.None);
        await apiService.StartAsync(CancellationToken.None);

        var client = new HttpClient
        {
            BaseAddress = new Uri(apiUrl)
        };

        await WaitForAsync(async () =>
        {
            var response = await client.GetAsync("/api/processes");
            return response.IsSuccessStatusCode ? client : null;
        });

        return new ApiFixture(client, coordinator, configPath);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonElement[]> WaitForEventsAsync(HttpClient client, Func<JsonElement[], bool> predicate, int timeoutMilliseconds = 5000, int pollMilliseconds = 50)
    {
        return await WaitForAsync(async () =>
        {
            var response = await client.GetAsync("/api/events?take=50");
            response.EnsureSuccessStatusCode();
            var events = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).EnumerateArray().ToArray();
            return predicate(events) ? events : null;
        }, timeoutMilliseconds, pollMilliseconds);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, int timeoutMilliseconds = 5000, int pollMilliseconds = 50)
        where T : class
    {
        var startedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - startedAt < TimeSpan.FromMilliseconds(timeoutMilliseconds))
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(pollMilliseconds);
        }

        throw new TimeoutException("Condition was not met before the timeout expired.");
    }

    private sealed record ApiError(string Code, string Message);

    private sealed record ApiFixture(HttpClient Client, SupervisorCoordinator Coordinator, string ConfigPath);

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "DeerHunter.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
