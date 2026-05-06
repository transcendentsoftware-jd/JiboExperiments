# Release `1.0.19` Plan

## Purpose

This release starts the shift from `1.0.18` hardening to visible feature growth.

The goal is to keep compatibility work steady while shipping personality and capability slices that make OpenJibo feel less like a placeholder cloud and more like a real assistant platform.

## Snapshot

- Kickoff date: `2026-05-05`
- Cloud version source of truth: [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- Active release constant: `1.0.19`

## Scope

### 1. Persona And Identity Surface

- add natural voice responses for robot identity/personality prompts
- start building reusable content hooks for question-vs-command style responses
- keep first implementation rule-based and test-backed

### 2. Reliability And Device Proof

- complete update/backup/restore proof path with captures and operator docs
- continue alarm/gallery/yes-no cleanup from `1.0.18` evidence where regressions are still open
- improve short-turn STT reliability and low-signal screening

### 3. Pegasus-To-Cloud Platform Porting

- prioritize small source-backed slices from Pegasus/JiboOS that can be shipped safely
- keep Nimbus and stock payload compatibility as the release guardrail
- avoid broad subsystem rewrites without tests and live-capture evidence

### 4. Holidays And Seasonal Personality

- port holiday-aware personality responses as a visible extension of the new persona slice
- start with a small, source-backed set (for example birthdays/holidays already represented in legacy data paths)
- ensure holiday responses feel characterful while still routing through stock-compatible payloads

### 5. Multi-Tenant Memory Storage Foundation

- define tenant boundaries across account, loop, device, and person-memory records
- add storage abstractions that can move from in-memory/local JSON to hosted SQL/Blob without reworking behavior layers
- implement memory-ready schemas and repository contracts for user facts (names, birthdays, personal dates, preferences) with strict tenant scoping

## First Implemented Slice In `1.0.19`

The first delivered slice in this release is persona expansion:

- `how old are you`
- `when's your birthday`
- `do you have a personality`
- `make a pizza`

`make a pizza` is now wired to the legacy scripted-response identity (`RA_JBO_MakePizza`) with pizza-making animation ESML, based on the original skill manifests.

This slice is intentionally small and user-visible. It creates immediate personality gains while we keep deeper platform work in parallel.

## Next Slices

1. Command-vs-question personality split (start with dance/twerk-style prompts, keep commands action-oriented and questions conversational)
2. First memory-backed personal facts (tenant-scoped birthday/preferences storage contracts + initial implementation)
3. Proactivity selector baseline (source-backed first proactive offers with safe throttling and stock-compatible payloads)
4. Dialog parsing expansion (more phrase variants, ambiguity handling, and transcript-to-intent guardrails)
5. Holidays and seasonal personality slice (time-scoped content backed by the new memory/proactivity path)
6. Update/backup/restore end-to-end proof (operator-run and documented)
7. STT noise-screening and short-utterance reliability pass
8. Provider-backed news/weather expansion using Pegasus-backed contracts
9. Capture indexing and retention boundary for group testing

For slices 1-5, use Pegasus phrase lists, MIM IDs, and behavior patterns as the source anchor before broadening into OpenJibo-native improvements.

## Definition Of Done

Release `1.0.19` is complete when:

- planned slices have focused tests and updated docs
- regression checklist passes for the existing stock-OS compatibility paths
- live runs confirm no critical regressions in alarms, gallery, yes/no, and cloud-version diagnostics
- memory/personality storage proves tenant isolation by account/loop/device boundaries and is compatible with the target hosted cloud footprint
