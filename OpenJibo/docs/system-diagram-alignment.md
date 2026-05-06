# System Diagram Alignment

## Purpose

This document maps the legacy Pegasus/Jibo cloud `system_diagram.png` architecture to the current OpenJibo `1.0.19` cloud.

Use it to keep release planning grounded in three views:

- where we were (legacy design intent)
- where we are (current hosted `.NET` implementation)
- where we are headed (next architecture slices)

As-of date: `2026-05-06`

## Diagram Inputs

- Legacy system architecture: `C:\Projects\jibo\pegasus\resources\system_diagram.png`
- Legacy generic skill scaffold: `C:\Projects\jibo\pegasus\packages\template-skill\docs\TemplateSkill.png`

## Template Skill Verdict

The template-skill diagram is a generic scaffold, not a production behavior contract.

Evidence:

- `C:\Projects\jibo\pegasus\packages\template-skill\src\TemplateSkill.ts` is a starter graph (`Intent Split` -> `Do MIM` -> `Complete` -> `Done`).
- `C:\Projects\jibo\pegasus\packages\template-skill\src\nodes\MemoSplitNode.ts` uses placeholder memo validation (`SomeThing`).

Conclusion: do not treat template-skill flow as a port target. Treat it as a shape reference only.

## System Diagram Mapping

| Legacy block | OpenJibo `1.0.19` equivalent | Current gap / opportunity |
| --- | --- | --- |
| `Auth` | [JiboCloudProtocolService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboCloudProtocolService.cs) (`CreateHubToken`, `CreateAccessToken`, account handlers) | move from in-memory/session stubs to durable tenant/account identity services |
| `Loop` | [JiboCloudProtocolService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboCloudProtocolService.cs) (`HandleLoop`) + [InMemoryCloudStateStore.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Persistence/InMemoryCloudStateStore.cs) | richer loop/member lifecycle and onboarding flows |
| `Hub` | [JiboWebSocketService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboWebSocketService.cs) + [WebSocketTurnFinalizationService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/WebSocketTurnFinalizationService.cs) | split hub responsibilities into clearer protocol, routing, and orchestration boundaries |
| `ASR Handler` | STT strategy selection in [WebSocketTurnFinalizationService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/WebSocketTurnFinalizationService.cs) + DI in [ServiceCollectionExtensions.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs) | short-turn reliability, managed STT comparison, and better low-signal/noise handling |
| `Parser / Robust Parser` | rule-based intent resolution in [JiboInteractionService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboInteractionService.cs) + focused state machines (personal report/chitchat) | deeper phrase import from Pegasus intents/entities plus ambiguity guardrails |
| `Skill Router` | [JiboInteractionService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboInteractionService.cs) decision switch and local skill payload shaping | external skill routing config and safer declarative intent mapping |
| `Proactivity Selector` | weighted candidate selection in [JiboInteractionService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/JiboInteractionService.cs) + pending-offer session state in [WebSocketTurnFinalizationService.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/WebSocketTurnFinalizationService.cs) | externalized proactivity catalog, cooldown policy, and broader category coverage |
| `Skill Registry` | implicit in current code/routing | formal registry abstraction for local/cloud capabilities and manifest metadata |
| `History` | tenant-scoped memory store in [InMemoryPersonalMemoryStore.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Persistence/InMemoryPersonalMemoryStore.cs) | durable multi-tenant persistence and history timeline/query support |
| `Lasso` provider aggregation | partial provider integration via weather provider wiring in [ServiceCollectionExtensions.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs) | full aggregation service for weather/news/calendar/knowledge inputs |
| `Proactivity Catalog` | in-code candidate lists/weights | explicit catalog service with tuned weights and operator controls |
| `Audio Logs` | file telemetry sinks in infrastructure telemetry | hosted indexed capture/retention for multi-operator analysis |

## Where We Were

Legacy cloud design was service-oriented around:

- hub orchestration
- parser robustness
- skill routing
- proactivity selection
- history/memory and provider aggregation

It emphasized a personality-rich surface while still being operationally observable.

## Where We Are

OpenJibo `1.0.19` is a functional hosted `.NET` modular monolith with:

- protocol compatibility paths for HTTP and websocket robot flows
- deterministic intent routing plus state-machine slices
- tenant-scoped memory foundation
- first proactivity baseline
- first external weather provider integration

This is the right shape for rapid parity plus safe incremental growth.

## Where We Are Headed

Near-term architecture evolution should preserve current shipping velocity:

1. Expand parser coverage and ambiguity guardrails from Pegasus phrase corpora.
2. Externalize proactivity policy and category catalogs.
3. Move memory from in-memory to durable multi-tenant backing stores.
4. Add stronger observability around STT, parser decisions, and follow-up turn state.
5. Build a focused aggregation layer (Lasso-like) for multi-provider content.

## Charm Preservation Rules

To keep Jibo's charm while modernizing the platform:

- keep MIM/ESML and expressive animation hooks as first-class outputs
- keep deterministic command-vs-question behavior for personality reliability
- layer richer provider data behind stable personality and gesture patterns
- prefer small source-backed slices over broad rewrites

## Queued Next `1.0.19` Task

The next queued implementation task is:

- `Dialog parsing expansion and ambiguity guardrails`

Tracking anchors:

- [release-1.0.19-plan.md](release-1.0.19-plan.md)
- [feature-backlog.md](feature-backlog.md)

Primary objective:

- import Pegasus parser intent phrases/entities to improve intent confidence while preserving command-vs-question personality behavior.
