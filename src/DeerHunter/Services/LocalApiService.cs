using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeerHunter.Configuration;
using DeerHunter.Models;
using Microsoft.Extensions.Options;

namespace DeerHunter.Services;

public sealed class LocalApiService : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SupervisorCoordinator _coordinator;
    private readonly ILogger<LocalApiService> _logger;
    private readonly HttpListener _listener = new();
    private readonly int _maxEvents;

    public LocalApiService(IOptions<DeerHunterOptions> options, SupervisorCoordinator coordinator, ILogger<LocalApiService> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
        _maxEvents = options.Value.EventBufferSize;

        foreach (var url in options.Value.Api.Urls)
        {
            _listener.Prefixes.Add(NormalizePrefix(url));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        _logger.LogInformation("Local API listening on: {Urls}", string.Join(", ", _listener.Prefixes));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener.IsListening)
        {
            _listener.Close();
        }

        return base.StopAsync(cancellationToken);
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

            if (request.HttpMethod == HttpMethod.Get.Method && string.Equals(path, "/api/processes", StringComparison.OrdinalIgnoreCase))
            {
                var processes = _coordinator.GetSnapshots()
                    .Select(static snapshot => new ProcessResponse(
                        snapshot.Name,
                        string.IsNullOrWhiteSpace(snapshot.Description) ? snapshot.Name : snapshot.Description,
                        snapshot))
                    .ToArray();

                await WriteJsonAsync(context.Response, HttpStatusCode.OK, processes, cancellationToken);
                return;
            }

            if (request.HttpMethod == HttpMethod.Get.Method && string.Equals(path, "/api/events", StringComparison.OrdinalIgnoreCase))
            {
                var take = ParseTake(request.QueryString["take"]);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, _coordinator.GetRecentEvents(take), cancellationToken);
                return;
            }

            if (TryMatchProcessRoute(path, out var processId, out var action))
            {
                switch ((request.HttpMethod, action))
                {
                    case ("POST", "start"):
                        _logger.LogInformation("Manual API action {Action} requested for process {ProcessId}.", "start", processId);
                        await HandleProcessActionAsync(context.Response, processId, "manual api start",
                            token => _coordinator.StartProcessAsync(processId, "manual api start", token), cancellationToken);
                        return;
                    case ("POST", "stop"):
                        _logger.LogInformation("Manual API action {Action} requested for process {ProcessId}.", "stop", processId);
                        await HandleProcessActionAsync(context.Response, processId, "manual api stop",
                            token => _coordinator.StopProcessAsync(processId, "manual api stop", token), cancellationToken);
                        return;
                    case ("POST", "restart"):
                        _logger.LogInformation("Manual API action {Action} requested for process {ProcessId}.", "restart", processId);
                        await HandleProcessActionAsync(context.Response, processId, "manual api restart",
                            token => _coordinator.RestartProcessAsync(processId, "manual api restart", token), cancellationToken);
                        return;
                    case ("POST", "priority"):
                        _logger.LogInformation("Manual API action {Action} requested for process {ProcessId}.", "priority", processId);
                        await HandlePriorityAsync(context, processId, cancellationToken);
                        return;
                }
            }

            _logger.LogWarning("Local API route not found for {Method} {Path}.", request.HttpMethod, path);
            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "not_found", "The requested endpoint was not found.", cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Local API request failed.");

            if (context.Response.OutputStream.CanWrite)
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, "server_error", "The local API request failed.", cancellationToken);
            }
        }
    }

    private async Task HandleProcessActionAsync(
        HttpListenerResponse response,
        string processId,
        string action,
        Func<CancellationToken, Task<ManagedProcessSnapshot?>> operation,
        CancellationToken cancellationToken)
    {
        var snapshot = await operation(cancellationToken);
        if (snapshot is null)
        {
            _logger.LogWarning("Manual API action {Action} referenced unknown process {ProcessId}.", action, processId);
            await WriteUnknownProcessAsync(response, processId, cancellationToken);
            return;
        }

        await WriteJsonAsync(response, HttpStatusCode.OK, new ProcessActionResponse(action, snapshot), cancellationToken);
    }

    private async Task HandlePriorityAsync(HttpListenerContext context, string processId, CancellationToken cancellationToken)
    {
        PriorityRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync<PriorityRequest>(context.Request.InputStream, SerializerOptions, cancellationToken);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, "invalid_request", "Priority request body must be valid JSON.", cancellationToken);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Priority) ||
            !Enum.TryParse<ManagedProcessPriority>(request.Priority, ignoreCase: true, out var priority))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, "invalid_priority", "Priority must be one of the configured managed process priority names.", cancellationToken);
            return;
        }

        var snapshot = await _coordinator.SetPriorityAsync(processId, priority, "manual api priority", cancellationToken);
        if (snapshot is null)
        {
            _logger.LogWarning("Manual API priority action referenced unknown process {ProcessId}.", processId);
            await WriteUnknownProcessAsync(context.Response, processId, cancellationToken);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new ProcessActionResponse("manual api priority", snapshot), cancellationToken);
    }

    private static bool TryMatchProcessRoute(string path, out string processId, out string action)
    {
        processId = string.Empty;
        action = string.Empty;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 5 &&
            string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "processes", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[3], "actions", StringComparison.OrdinalIgnoreCase))
        {
            processId = Uri.UnescapeDataString(segments[2]);
            action = segments[4];
            return true;
        }

        if (segments.Length == 4 &&
            string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "processes", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[3], "priority", StringComparison.OrdinalIgnoreCase))
        {
            processId = Uri.UnescapeDataString(segments[2]);
            action = "priority";
            return true;
        }

        return false;
    }

    private int ParseTake(string? rawTake)
    {
        if (!int.TryParse(rawTake, out var take) || take <= 0)
        {
            return Math.Min(100, _maxEvents);
        }

        return Math.Min(take, _maxEvents);
    }

    private static string NormalizePrefix(string url)
    {
        if (!url.EndsWith('/'))
        {
            url += "/";
        }

        return url;
    }

    private async Task WriteUnknownProcessAsync(HttpListenerResponse response, string processId, CancellationToken cancellationToken)
    {
        await WriteErrorAsync(response, HttpStatusCode.NotFound, "unknown_process", $"Unknown process identifier '{processId}'.", cancellationToken);
    }

    private async Task WriteJsonAsync<T>(HttpListenerResponse response, HttpStatusCode statusCode, T payload, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, payload, SerializerOptions, cancellationToken);
        response.Close();
    }

    private Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, string code, string message, CancellationToken cancellationToken)
        => WriteJsonAsync(response, statusCode, new ErrorResponse(code, message), cancellationToken);

    private sealed record ProcessResponse(string Id, string Name, ManagedProcessSnapshot Snapshot);

    private sealed record ProcessActionResponse(string Action, ManagedProcessSnapshot Snapshot);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record PriorityRequest(string? Priority);
}
