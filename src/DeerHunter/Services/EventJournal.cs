using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DeerHunter.Configuration;
using DeerHunter.Models;
using Microsoft.Extensions.Options;

namespace DeerHunter.Services;

public sealed class EventJournal : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Channel<SupervisorEvent> _channel = Channel.CreateUnbounded<SupervisorEvent>();
    private readonly ILogger<EventJournal> _logger;
    private readonly string _journalPath;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _writerTask;

    public EventJournal(IOptions<DeerHunterOptions> options, IHostEnvironment environment, ILogger<EventJournal> logger)
    {
        _logger = logger;
        _journalPath = Path.GetFullPath(options.Value.JournalPath, environment.ContentRootPath);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        _writerTask = Task.Run(WriteLoopAsync, cancellationToken);
        return Task.CompletedTask;
    }

    public ValueTask EnqueueAsync(SupervisorEvent supervisorEvent)
    {
        return _channel.Writer.WriteAsync(supervisorEvent);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        if (_writerTask is not null)
        {
            await _writerTask.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_writerTask is not null)
        {
            await _writerTask;
        }

        _shutdown.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        await using var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream);

        try
        {
            await foreach (var supervisorEvent in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                var json = JsonSerializer.Serialize(supervisorEvent, SerializerOptions);
                await writer.WriteLineAsync(json);
                await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to write event journal to {JournalPath}", _journalPath);
        }
    }
}
