# Protocol Inventory

## Purpose

This document tracks the currently observed cloud surface area for Jibo and helps keep the .NET port aligned with real behavior captured by the Node prototype.

It is not a claim that the current Node server covers all Jibo endpoints or behaviors. It reflects only the portions mapped so far.

Confidence levels:

- `high`: observed in code and currently represented in the .NET scaffold
- `medium`: observed in the Node oracle and documented, but not fully ported yet
- `low`: expected or inferred, needs more robot validation

## Known Hosts

| Host | Purpose | Confidence | Notes |
| --- | --- | --- | --- |
| `api.jibo.com` | HTTPS API target for `X-Amz-Target` operations | high | Main request dispatch path in the Node prototype |
| `api-socket.jibo.com` | token-authenticated WebSocket path | medium | Node accepts tokenized connections and intentionally sends no greeting |
| `neo-hub.jibo.com` | listen and proactive WebSocket traffic | medium | Path-driven split between listen and `/v1/proactive` |

## Region Configuration

Current robot findings suggest the preferred OpenJibo bootstrap path is to inject a new region configuration rather than treat host overrides as the only integration seam.

Confirmed or strongly observed files:

- `/etc/jibo-jetstream-service.json`
- `/var/jibo/credentials.json`
- `/etc/jibo-ssm/*.json`
- `/skills/jibo/Jibo/Skills/@be/be/node_modules/language-subtag-registry/data/json/registry.json`
- `/skills/jibo/Jibo/Skills/oobe-config/config.json`

The first two are the clearest current OpenJibo injection points. The others should remain on the audit list while endpoint and behavior mapping continues.

## HTTP Dispatch Families

Observed from `open-jibo-link.js`:

| Service family | Example operations | Confidence | Current .NET status |
| --- | --- | --- | --- |
| `Account_*` | `CreateHubToken`, `CreateAccessToken`, `Login`, `Get` | high | initial dispatch implemented |
| `Notification_*` | `NewRobotToken` | high | initial dispatch implemented |
| `Loop_*` | `List`, `ListLoops` | medium | initial dispatch implemented |
| `Robot_*` | `GetRobot`, `UpdateRobot` | medium | initial dispatch implemented |
| `Update_*` | `ListUpdates`, `ListUpdatesFrom`, `GetUpdateFrom`, `CreateUpdate`, `RemoveUpdate` | medium | list/get scaffolding implemented |
| `Media_20160725` | `List`, `Get`, `Create`, `Remove` | medium | not yet ported |
| `Log_*` | `PutEvents`, `PutEventsAsync`, `PutBinaryAsync`, `PutAsrBinary` | medium | upload endpoints reserved; detailed handling pending |
| `Key_*` | `ShouldCreate`, `CreateSymmetricKey`, `GetRequest` | medium | pending |
| `Person_*` | `ListHolidays` | low | pending |
| `Backup_*` | `List` | low | pending |

## WebSocket Flows

| Host/path | Flow | Confidence | Current .NET status |
| --- | --- | --- | --- |
| `api-socket.jibo.com/{token}` | token-authenticated socket for API-side signaling | medium | stub endpoint implemented |
| `neo-hub.jibo.com/{listen-path}` | listen turn flow with JSON and binary audio traffic | medium | initial JSON flow implemented |
| `neo-hub.jibo.com/v1/proactive` | proactive connection flow | medium | stub endpoint implemented |

## Upload Paths

| Path | Purpose | Confidence | Current .NET status |
| --- | --- | --- | --- |
| `/upload/asr-binary` | async audio/log upload target | medium | placeholder endpoint accepted |
| `/upload/log-events` | async log upload target | medium | placeholder endpoint accepted |
| `/upload/log-binary` | async binary upload target | medium | placeholder endpoint accepted |

## First Core Revive Slice

The first .NET hosted milestone should fully support:

- `Account.CreateHubToken`
- `Notification.NewRobotToken`
- `Loop.List` and `Loop.ListLoops`
- `Robot.GetRobot`
- `Update.ListUpdates`, `Update.ListUpdatesFrom`, `Update.GetUpdateFrom`
- root probe and health checks
- basic listen/proactive WebSocket acceptance
- normalized turn and reply mapping for simple chat

## Known Beyond Current Node Coverage

The platform scope is broader than the endpoints currently modeled in `open-jibo-link.js`. Known areas that still need mapping include:

- broader skill launch and lifecycle behavior
- interactivity command families beyond the joke starter path
- richer animation and expression control
- ESML and embodied speech features
- additional service families and region-specific endpoint behavior
- startup and configuration differences across Jibo software variants

Useful external references for future mapping:

- [Speak-Tweak Docs](https://hri2024.jibo.media.mit.edu/Speak-Tweak-Docs)
- [ESML PDF](https://hri2024.jibo.media.mit.edu/attachments/SDK-SDK---ESML-121023-203758.pdf)

## Fixture Source

Sanitized fixtures live under [src/Jibo.Cloud/node/fixtures](C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/node/fixtures) and should be expanded as real traffic is captured.
