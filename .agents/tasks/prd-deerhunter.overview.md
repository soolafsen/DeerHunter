# PRD Overview: DeerHunter

- File: .agents\tasks\prd-deerhunter.json
- Stories: 6 total (6 open, 0 in_progress, 0 done)

## Quality Gates
- dotnet build
- dotnet test
- dotnet run -- --help

## Stories
- [open] US-001: Create runnable DeerHunter worker host
- [open] US-002: Supervise configured child processes (depends on: US-001)
- [open] US-003: Tail external log files and unify event ingestion (depends on: US-002)
- [open] US-004: Match signal patterns and execute reactions (depends on: US-003)
- [open] US-005: Expose local monitoring and manual control API (depends on: US-002, US-004)
- [open] US-006: Harden for multi-day runs and debugging (depends on: US-005)
