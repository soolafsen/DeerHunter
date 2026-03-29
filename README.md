# DeerHunter

DeerHunter is a Windows-first .NET supervisor for child processes, scripts, executables, helper processes, and AI agent processes. It monitors stdout, stderr, and optional external log files, reacts to matched signals, and includes a built-in localhost dashboard.

## What It Does

- supervises managed child processes and helpers
- tails stdout, stderr, and external logs into one event pipeline
- matches signal rules and reacts with restart, kill, priority changes, helper control, and condition changes
- exposes localhost control APIs
- serves a built-in dashboard for inspection and control
- exposes DeerHunter host actions, not just child-process actions

## Dashboard

Run DeerHunter and open the configured localhost URL, typically [http://127.0.0.1:5078/](http://127.0.0.1:5078/).

The dashboard can:

- inspect DeerHunter host state
- inspect managed process and helper state
- inspect recent event history
- start, stop, restart, and reprioritize child processes
- pause supervision, resume supervision, reload configuration, and request clean shutdown of DeerHunter itself

## Build

```powershell
dotnet build DeerHunter.slnx
```

## Test

```powershell
dotnet test DeerHunter.slnx
```

## Run

Run the worker with the default config:

```powershell
dotnet run --project src/DeerHunter
```

Show help:

```powershell
dotnet run --project src/DeerHunter -- --help
```

Use a different config file:

```powershell
dotnet run --project src/DeerHunter -- --config deerhunter.json
```
