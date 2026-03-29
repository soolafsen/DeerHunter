namespace DeerHunter.Services;

public sealed class HostRuntimeState
{
    public HostRuntimeState(string configPath)
    {
        ConfigPath = configPath;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public string ConfigPath { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public bool IsSupervisionPaused { get; private set; }

    public bool ShutdownRequested { get; private set; }

    public void PauseSupervision() => IsSupervisionPaused = true;

    public void ResumeSupervision() => IsSupervisionPaused = false;

    public void MarkShutdownRequested() => ShutdownRequested = true;
}
