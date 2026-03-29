using DeerHunter.Configuration;
using DeerHunter.Models;

namespace DeerHunter.Services;

public sealed class ExternalLogTailer : IAsyncDisposable
{
    private readonly ExternalLogOptions _options;
    private readonly string _path;
    private readonly Action<ObservedLine> _publish;
    private readonly SupervisorCoordinator _coordinator;
    private readonly string _processName;
    private readonly ILogger _logger;
    private Task? _task;
    private string _partialLine = string.Empty;
    private long _position;
    private bool _warnedMissing;
    private bool _hasAttachedToFile;
    private bool _wasMissingAfterAttach;
    private DateTimeOffset? _activeFileCreatedUtc;

    public ExternalLogTailer(
        ExternalLogOptions options,
        string path,
        Action<ObservedLine> publish,
        SupervisorCoordinator coordinator,
        string processName,
        ILogger logger)
    {
        _options = options;
        _path = path;
        _publish = publish;
        _coordinator = coordinator;
        _processName = processName;
        _logger = logger;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _task = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_task is not null)
        {
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (!_warnedMissing)
                    {
                        _warnedMissing = true;
                        _coordinator.RecordEvent("external-log.missing", _processName, "external", $"External log {_options.Name} is missing.",
                            new Dictionary<string, string?> { ["path"] = _path, ["externalLogName"] = _options.Name });
                    }

                    if (_hasAttachedToFile)
                    {
                        _wasMissingAfterAttach = true;
                        _activeFileCreatedUtc = null;
                    }

                    continue;
                }

                _warnedMissing = false;
                var fileInfo = new FileInfo(_path);
                var shouldStartAtEnd = !_hasAttachedToFile && _options.StartPosition == ExternalLogStartPosition.End;
                var fileWasRecreated = _wasMissingAfterAttach ||
                                       (_activeFileCreatedUtc.HasValue && fileInfo.CreationTimeUtc != _activeFileCreatedUtc.Value) ||
                                       fileInfo.Length < _position;

                if (fileWasRecreated)
                {
                    ResetPosition();
                    _coordinator.RecordEvent("external-log.rotated", _processName, "external", $"External log {_options.Name} rotated or was recreated.",
                        new Dictionary<string, string?> { ["path"] = _path, ["externalLogName"] = _options.Name });
                }

                _hasAttachedToFile = true;
                _wasMissingAfterAttach = false;
                _activeFileCreatedUtc = fileInfo.CreationTimeUtc;

                if (shouldStartAtEnd)
                {
                    _position = fileInfo.Length;
                    continue;
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(_position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);

                var text = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(text))
                {
                    _position = stream.Position;
                    continue;
                }

                _position = stream.Position;

                var combined = _partialLine + text;
                var lines = combined.Split(["\r\n", "\n"], StringSplitOptions.None);
                _partialLine = combined.EndsWith('\n') ? string.Empty : lines[^1];

                var completeCount = _partialLine.Length == 0 ? lines.Length : lines.Length - 1;
                for (var index = 0; index < completeCount; index++)
                {
                    if (lines[index].Length == 0)
                    {
                        continue;
                    }

                    _publish(new ObservedLine("external", lines[index], _options.Name));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed while tailing {Path}", _path);
                _coordinator.RecordEvent("external-log.error", _processName, "external", $"Failed while tailing {_options.Name}.",
                    new Dictionary<string, string?> { ["path"] = _path, ["error"] = exception.Message, ["externalLogName"] = _options.Name });
            }
        }
    }

    private void ResetPosition()
    {
        _position = 0;
        _partialLine = string.Empty;
    }
}
