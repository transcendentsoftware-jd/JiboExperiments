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

## Second Implemented Slice In `1.0.19`

The second delivered slice is first tenant-scoped personal memory:

- store birthday from phrases like `my birthday is April 12`
- recall birthday from phrases like `when is my birthday`
- store preferences from phrases like `my favorite music is jazz`
- recall preferences from phrases like `what is my favorite music`

Memory keys are scoped by account/loop/device tenant context so one tenant does not leak into another.

## Third Implemented Slice In `1.0.19`

The third delivered slice starts memory-triggered proactivity and broadens memory parsing:

- `surprise me` now runs a weighted proactivity selector
- selectors use tenant-scoped memory signals (favorites and likes/dislikes) plus date triggers
- February 9 (`National Pizza Day`) can proactively launch the pizza animation path
- proactive pizza fact offer flow now stores pending offer state and resolves direct `yes` / `no` follow-up answers
- memory parsing now covers:
  - names (`my name is ...`, `what is my name`)
  - important dates (`our anniversary is ...`, `when is our anniversary`)
  - likes/dislikes (`i like ...`, `i love ...`, `i dislike ...`, `i don't like ...`)
  - favorite phrase variants including reverse form (`pizza is my favorite food`)

## Fourth Implemented Slice In `1.0.19`

The fourth delivered slice starts weather compatibility using Pegasus-style report-skill routing:

- weather phrases now route to `report-skill` instead of generic placeholder chat
- outbound NLU launch uses legacy reactive intent `requestWeatherPR` (source-aligned with Pegasus manifests/tests)
- weather entity hints are added for:
  - `date = tomorrow` on tomorrow phrasing
  - `Weather = rain|snow|...` on condition questions (for example `will it rain tomorrow`)
- websocket output now emits local skill redirect + silent completion for weather launch, matching existing local-skill launch patterns

## Fifth Implemented Slice In `1.0.19`

The fifth delivered slice adds provider-backed weather content while preserving Pegasus launch compatibility:

- OpenWeather provider abstraction and infrastructure wiring are added to the hosted cloud
- weather requests still launch `report-skill` with `requestWeatherPR` and legacy weather/date entities
- weather replies now include cloud-generated spoken summaries from provider data:
  - current conditions (`Right now in ...`)
  - tomorrow forecast shape (`Tomorrow in ...`) with high/low temperatures when available
- simple location extraction is supported for phrasing like `what's the weather in Chicago tomorrow`
- provider config supports appsettings and `OPENWEATHER_API_KEY` environment fallback for deployment

## Next Slices

1. Dialog parsing expansion (more phrase variants, ambiguity handling, and transcript-to-intent guardrails)
2. Holidays and seasonal personality slice beyond pizza day (time-scoped content backed by memory/proactivity path)
3. Durable memory persistence path (swap in provider-backed multi-tenant storage while preserving behavior contracts)
4. Update/backup/restore end-to-end proof (operator-run and documented)
5. STT noise-screening and short-utterance reliability pass
6. Provider-backed news expansion and deeper weather parity using Pegasus-backed contracts
7. Capture indexing and retention boundary for group testing

For slices 1-5, use Pegasus phrase lists, MIM IDs, and behavior patterns as the source anchor before broadening into OpenJibo-native improvements.

## Definition Of Done

Release `1.0.19` is complete when:

- planned slices have focused tests and updated docs
- regression checklist passes for the existing stock-OS compatibility paths
- live runs confirm no critical regressions in alarms, gallery, yes/no, and cloud-version diagnostics
- memory/personality storage proves tenant isolation by account/loop/device boundaries and is compatible with the target hosted cloud footprint
