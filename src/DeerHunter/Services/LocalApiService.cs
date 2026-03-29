using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeerHunter.Configuration;
using DeerHunter.Models;
using Microsoft.Extensions.Options;

namespace DeerHunter.Services;

public sealed class LocalApiService : BackgroundService
{
    private static readonly IReadOnlyDictionary<string, string> DashboardContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SupervisorCoordinator _coordinator;
    private readonly ILogger<LocalApiService> _logger;
    private readonly HttpListener _listener = new();
    private readonly int _maxEvents;
    private readonly string _dashboardRoot = Path.Combine(AppContext.BaseDirectory, "Dashboard");

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

            if (request.HttpMethod == HttpMethod.Get.Method && await TryServeDashboardAsync(context.Response, path, cancellationToken))
            {
                return;
            }

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

            if (string.Equals(path, "/api/host/status", StringComparison.OrdinalIgnoreCase))
            {
                if (request.HttpMethod != HttpMethod.Get.Method)
                {
                    await WriteMethodNotAllowedAsync(context.Response, "GET", cancellationToken);
                    return;
                }

                await WriteJsonAsync(context.Response, HttpStatusCode.OK, _coordinator.GetHostStatus(), cancellationToken);
                return;
            }

            if (TryMatchHostActionRoute(path, out var hostAction))
            {
                if (request.HttpMethod != HttpMethod.Post.Method)
                {
                    await WriteMethodNotAllowedAsync(context.Response, "POST", cancellationToken);
                    return;
                }

                var result = hostAction.ToLowerInvariant() switch
                {
                    "pause" => _coordinator.PauseSupervision("host.api", "manual api pause supervision"),
                    "resume" => _coordinator.ResumeSupervision("host.api", "manual api resume supervision"),
                    "reload" => _coordinator.ReloadConfiguration("host.api", "manual api reload configuration"),
                    "shutdown" => _coordinator.RequestCleanShutdown("host.api", "manual api clean shutdown"),
                    _ => null
                };

                if (result is null)
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, "unknown_host_action", $"Unknown host action '{hostAction}'.", cancellationToken);
                    return;
                }

                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new HostActionResponse(hostAction, result), cancellationToken);
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
            if (request.HttpMethod == HttpMethod.Get.Method && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                await WriteDashboardNotFoundAsync(context.Response, path, cancellationToken);
                return;
            }

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

    private static bool TryMatchHostActionRoute(string path, out string action)
    {
        action = string.Empty;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 4 &&
            string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], "actions", StringComparison.OrdinalIgnoreCase))
        {
            action = segments[3];
            return true;
        }

        return false;
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

    private async Task<bool> TryServeDashboardAsync(HttpListenerResponse response, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path) || string.Equals(path, "/", StringComparison.Ordinal))
        {
            await WriteDashboardFileAsync(response, Path.Combine(_dashboardRoot, "index.html"), cancellationToken);
            return true;
        }

        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var assetPath = Path.Combine(_dashboardRoot, relativePath);
        if (!File.Exists(assetPath))
        {
            return false;
        }

        await WriteDashboardFileAsync(response, assetPath, cancellationToken);
        return true;
    }

    private Task WriteMethodNotAllowedAsync(HttpListenerResponse response, string allowedMethod, CancellationToken cancellationToken)
    {
        response.Headers["Allow"] = allowedMethod;
        return WriteErrorAsync(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", $"This endpoint requires HTTP {allowedMethod}.", cancellationToken);
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

    private async Task WriteDashboardFileAsync(HttpListenerResponse response, string filePath, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = DashboardContentTypes.TryGetValue(Path.GetExtension(filePath), out var contentType)
            ? contentType
            : "application/octet-stream";

        await using var fileStream = File.OpenRead(filePath);
        await fileStream.CopyToAsync(response.OutputStream, cancellationToken);
        response.Close();
    }

    private async Task WriteDashboardNotFoundAsync(HttpListenerResponse response, string path, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.NotFound;
        response.ContentType = "text/plain; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync($"Dashboard asset '{path}' was not found.");
        await writer.FlushAsync(cancellationToken);
        response.Close();
    }

    private sealed record ProcessResponse(string Id, string Name, ManagedProcessSnapshot Snapshot);

    private sealed record ProcessActionResponse(string Action, ManagedProcessSnapshot Snapshot);

    private sealed record HostActionResponse(string Action, HostStatusSnapshot Host);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record PriorityRequest(string? Priority);
}
