# DeerHunter Improvement Suggestions

Last updated: 2026-03-29 04:35 Europe/Oslo

## Purpose

This file is a forward-looking backlog for DeerHunter after the current supervisor, API, and dashboard work is complete.

It is intentionally written so the next agent can pick up from here without reconstructing context from chat history.

## Current Baseline

DeerHunter already has:

- a Windows-first .NET worker host
- managed child-process and helper supervision
- stdout, stderr, and external log ingestion
- signal matching and reactions
- bounded event retention and JSONL journaling
- localhost API for child and host actions
- built-in dashboard for state, events, and controls

Repo references:

- `docs/agent-handoff.md`
- `docs/workdiary.md`
- `.agents/tasks/prd-deerhunter.json`
- `.agents/tasks/prd-deerhunter-dashboard.json`

## Improvement Themes

These are suggestions, not committed roadmap promises.

### 1. Runtime robustness

Why:

- long-running process supervisors usually fail at lifecycle edges, not happy-path launches
- DeerHunter should become boring under churn, log storms, and misbehaving children

Suggested work:

- add watchdogs for hung stop and hung restart flows
- add per-process backoff policies beyond fixed delay
- add optional max-runtime and restart-window limits
- add stronger child cleanup for orphaned descendants
- add startup dependency ordering between processes and helpers

Priority:

- high

### 2. Config reload that actually applies changes

Why:

- reload is currently a control-plane action, but safe live application of config changes is where the real value is

Suggested work:

- support add/update/remove of process definitions on reload
- diff rules and external log watchers safely
- reject partial invalid reloads atomically
- surface reload diff results in events and dashboard

Priority:

- high

### 3. Better process semantics

Why:

- some children are not simple “run forever” workers
- AI agents and batch workers often need richer state than just running or stopped

Suggested work:

- support process groups or roles
- add expected-idle / batch-complete / cooling-down states
- allow health signals to promote or demote condition independently of lifecycle
- add optional success and failure exit-code policies

Priority:

- high

### 4. Dashboard usability

Why:

- the dashboard works, but operator surfaces get valuable quickly from small UX improvements

Suggested work:

- filtering and search in the event feed
- per-process detail drawer or page
- sticky action/result banner with timestamps
- color-coded severity and event categories
- manual refresh versus auto-refresh controls
- compact and dense layout modes

Priority:

- medium

### 5. Auditability and retrospective debugging

Why:

- one of DeerHunter’s real jobs is explaining what happened after something went wrong

Suggested work:

- event export endpoint
- per-process event slices
- correlation ids for user actions and resulting lifecycle events
- richer structured fields in journal entries
- dashboard links from state cards to relevant events

Priority:

- medium

### 6. Security and deployment hardening

Why:

- localhost-only is fine for now, but operational tooling tends to grow beyond one trusted desktop

Suggested work:

- optional auth even on localhost
- role-based control separation between read-only and operator actions
- explicit dangerous-action confirmation for kill and shutdown
- Windows service install / uninstall scripts
- signed release artifacts and publish profiles

Priority:

- medium

### 7. Cross-platform evaluation

Why:

- the current implementation is intentionally `.NET` and Windows-first
- later requirements may justify staying with it or reconsidering the runtime

Suggested work:

- write a short design note comparing `.NET`, `Go`, `Rust`, and `Python`
- make the decision based on operational needs, not novelty

Language guidance:

- stay on `.NET` unless there is a concrete need to move
- consider `Go` for simpler static deployment and smaller operational footprint
- consider `Rust` for tighter systems-level robustness requirements
- consider `Python` only if orchestration and AI integration outweigh service-runtime concerns

Priority:

- medium

### 8. AI-specific operator support without AI-specific orchestration

Why:

- AI children should stay generic processes, but operators may still need better visibility into them

Suggested work:

- agent-specific labels and metadata in config
- token/session metadata in the dashboard if emitted by logs
- quick links from an agent process to its log sources
- rule templates for common AI failure modes such as rate-limit loops or stuck retries

Priority:

- medium

### 9. Testing depth

Why:

- the project already has good focused coverage; the next gains come from scenario coverage

Suggested work:

- multi-process integration tests with helpers and reloads
- log-rotation edge-case tests
- dashboard browser tests for more action flows
- noisy-event retention stress tests
- service-host tests for install and run guidance

Priority:

- high

## Suggested Execution Order

If a future agent should continue improving DeerHunter, this is the recommended order:

1. Config reload that truly applies changes safely
2. Runtime robustness around stop, restart, and orphan cleanup
3. Better process semantics and policy handling
4. Auditability improvements
5. Dashboard usability improvements
6. Security and deployment hardening
7. Cross-platform/runtime evaluation note

## Fast Wins

These are good first tasks if the next agent needs a small, contained start:

- add event filtering in the dashboard
- add per-process event endpoint
- add correlation ids for manual actions
- add config reload diff event payloads
- add confirmation UI for host shutdown

## Bigger Bets

These are worth doing only with deliberate intent:

- live config reconciliation across added and removed processes
- richer process dependency model
- service packaging and distribution flow
- moving away from `.NET`

## Agent Pickup Notes

The next agent should start by reading:

1. `docs/agent-handoff.md`
2. `docs/workdiary.md`
3. `docs/improvement.md`
4. `.agents/tasks/prd-deerhunter-dashboard.json`

Then decide whether the next step is:

- a new Ralph PRD for one improvement theme
- a direct small implementation pass for a fast win
- a review pass to validate whether the current architecture still fits the next target

## Recommended Next PRD Candidates

If the next agent wants a clean Ralph-driven continuation, these are good candidate PRDs:

- `DeerHunter config reload reconciliation`
- `DeerHunter process policy and backoff improvements`
- `DeerHunter event audit and export improvements`
- `DeerHunter dashboard usability pass`
- `DeerHunter deployment and service packaging`
