# DeerHunter Agent Handoff

Last updated: 2026-03-29 04:24 Europe/Oslo

## Goal

Build DeerHunter as a Windows-first C# .NET child-process supervisor and localhost control plane that can:

- start and stop configured child processes
- watch `stdout`, `stderr`, and optional external log files
- react to matched signals by kill, restart, priority up or down, helper start or stop, and internal state changes
- run stably for long periods
- expose and operate state through a built-in dashboard

## Ralph Usage So Far

- `ralph prd ... --out .agents/tasks/prd-deerhunter.json`
- `ralph overview --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter.json`
- `ralph prd ... --out .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph overview --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`
- `ralph build 1 --no-commit --prd .agents/tasks/prd-deerhunter-dashboard.json`

Ralph completed all stories in both PRDs and updated `.ralph/progress.md`.

## Current State

- Workspace: `C:\Users\svein\source\repos\ralpStunts\worker01`
- Git repo exists locally and already has a published GitHub repo: `https://github.com/soolafsen/DeerHunter`
- GitHub CLI is installed and authenticated as `soolafsen`.
- Current codebase is a worker-style host with supervised child-process startup, exit tracking, stdout/stderr ingestion, external-log ingestion, rule matching, helper start/stop, restart reactions, host self-control, a localhost monitoring API, and a built-in dashboard.
- All known PRD stories are complete.

## Files That Matter Most

- `.agents/tasks/prd-deerhunter.json`
- `.agents/tasks/prd-deerhunter.overview.md`
- `.agents/tasks/prd-deerhunter-dashboard.json`
- `.agents/tasks/prd-deerhunter-dashboard.overview.md`
- `.ralph/progress.md`
- `docs/workdiary.md`
- `src/DeerHunter/Program.cs`
- `src/DeerHunter/Dashboard/index.html`
- `src/DeerHunter/Dashboard/dashboard.css`
- `src/DeerHunter/Dashboard/dashboard.js`
- `src/DeerHunter/Services/SupervisorCoordinator.cs`
- `src/DeerHunter/Services/HostRuntimeState.cs`
- `src/DeerHunter/Services/LocalApiService.cs`
- `src/DeerHunter/Services/ManagedProcessAgent.cs`
- `src/DeerHunter/Services/EventStore.cs`
- `src/DeerHunter/Services/EventJournal.cs`
- `src/DeerHunter/Services/ExternalLogTailer.cs`
- `src/DeerHunter/deerhunter.json`
- `tests/DeerHunter.Tests/HostConfigurationTests.cs`
- `tests/DeerHunter.Tests/LocalApiServiceTests.cs`
- `tests/DeerHunter.Tests/SupervisorCoordinatorTests.cs`

## Confirmed Decisions

- Keep the architecture simple and inspectable.
- Prefer one executable host path for console and Windows Service mode.
- Keep config file-based.
- Keep local event history bounded in memory.
- Persist retrospective event history to a JSONL journal.
- Treat helper processes as regular managed processes with metadata, not as a second orchestration system.
- Treat AI agents exactly like any other child process. They may be scripts, executables, or agent runtimes; DeerHunter should not grow a separate orchestration mode just because a child happens to be AI.
- Treat DeerHunter host actions as explicit control-plane actions, not as fake process actions.
- Keep the dashboard as plain static assets served by the existing local listener instead of introducing a heavyweight frontend stack.

## Recommended Next Move

1. Push the dashboard extension to the existing GitHub repo.
2. If later work is needed, start from `docs/workdiary.md`, `.agents/tasks/prd-deerhunter-dashboard.json`, and `.ralph/progress.md`.
