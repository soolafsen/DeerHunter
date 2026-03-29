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
        var eventStore = new EventStore(options);
        var eventJournal = new EventJournal(options, environment, NullLogger<EventJournal>.Instance);
        var coordinator = new SupervisorCoordinator(
            options,
            eventStore,
            eventJournal,
            environment,
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

        return new ApiFixture(client, coordinator);
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

    private sealed record ApiFixture(HttpClient Client, SupervisorCoordinator Coordinator);

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "DeerHunter.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
