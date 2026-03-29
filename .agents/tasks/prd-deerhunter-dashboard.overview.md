# PRD Overview: DeerHunter Local Dashboard

- File: .agents\tasks\prd-deerhunter-dashboard.json
- Stories: 6 total (6 open, 0 in_progress, 0 done)

## Quality Gates
- dotnet build DeerHunter.slnx
- dotnet test DeerHunter.slnx --no-build
- dotnet run --project src/DeerHunter -- --help

## Stories
- [open] US-001: Add host state and host action API
- [open] US-002: Serve a built-in localhost dashboard (depends on: US-001)
- [open] US-003: Show live host and managed process state (depends on: US-001, US-002)
- [open] US-004: Show recent event history in the dashboard (depends on: US-002)
- [open] US-005: Wire dashboard operational controls (depends on: US-001, US-002, US-003, US-004)
- [open] US-006: Harden runtime behavior and test the dashboard flow (depends on: US-001, US-002, US-003, US-004, US-005)
