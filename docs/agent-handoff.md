# DeerHunter Agent Handoff

Last updated: 2026-03-29 03:47 Europe/Oslo

## Goal

Build DeerHunter as a Windows-first C# .NET child-process supervisor that can:

- start and stop configured child processes
- watch `stdout`, `stderr`, and optional external log files
- react to matched signals by kill, restart, priority up or down, helper start or stop, and internal state changes
- run stably for long periods
- expose state cleanly enough for a later graphical monitoring panel

## Ralph Usage So Far

- `ralph prd ... --out .agents/tasks/prd-deerhunter.json`
- `ralph overview --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`

Ralph completed all PRD stories `US-001` through `US-006` and updated `.ralph/progress.md`.

## Current State

- Workspace: `C:\Users\svein\source\repos\ralpStunts\worker01`
- Git repo exists locally but nothing committed yet.
- GitHub CLI is installed and authenticated as `soolafsen`.
- Current codebase is a worker-style host with supervised child-process startup, exit tracking, stdout/stderr ingestion, external-log ingestion, rule matching, helper start/stop, restart reactions, a localhost monitoring API, and final hardening/tests in place.
- All PRD stories are complete and the repo is ready to publish.

## Files That Matter Most

- `.agents/tasks/prd-deerhunter.json`
- `.agents/tasks/prd-deerhunter.overview.md`
- `.ralph/progress.md`
- `src/DeerHunter/Program.cs`
- `src/DeerHunter/Services/SupervisorCoordinator.cs`
- `src/DeerHunter/Services/ManagedProcessAgent.cs`
- `src/DeerHunter/Services/EventStore.cs`
- `src/DeerHunter/Services/EventJournal.cs`
- `src/DeerHunter/Services/ExternalLogTailer.cs`
- `src/DeerHunter/deerhunter.json`
- `tests/DeerHunter.Tests/HostConfigurationTests.cs`

## Confirmed Decisions

- Keep the architecture simple and inspectable.
- Prefer one executable host path for console and Windows Service mode.
- Keep config file-based.
- Keep local event history bounded in memory.
- Persist retrospective event history to a JSONL journal.
- Treat helper processes as regular managed processes with metadata, not as a second orchestration system.
- Treat AI agents exactly like any other child process. They may be scripts, executables, or agent runtimes; DeerHunter should not grow a separate orchestration mode just because a child happens to be AI.

## Remaining Work By PRD

- `US-002` supervise configured child processes
- `US-003` tail external logs and unify ingestion
## Recommended Next Move

1. Create the GitHub repo `DeerHunter` and push the current green state.
2. If later work is needed, start from `docs/workdiary.md`, `.agents/tasks/prd-deerhunter.json`, and `.ralph/progress.md`.

## Publish Target

User explicitly asked for a new GitHub repo named `DeerHunter`.

Expected commands once the repo is ready:

```powershell
gh repo create DeerHunter --public --source . --remote origin --push
```

If the name already exists under the account, confirm the fallback before pushing.
