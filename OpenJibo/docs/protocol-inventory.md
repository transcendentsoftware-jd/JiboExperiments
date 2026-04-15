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
| `Media_20160725` | `List`, `Get`, `Create`, `Remove` | medium | implemented in current parity scaffold |
| `Log_*` | `PutEvents`, `PutEventsAsync`, `PutBinaryAsync`, `PutAsrBinary` | medium | async upload metadata and placeholder upload endpoints implemented |
| `Key_*` | `ShouldCreate`, `CreateSymmetricKey`, `GetRequest` | medium | implemented in current parity scaffold |
| `Person_*` | `ListHolidays` | low | implemented in current parity scaffold |
| `Backup_*` | `List` | low | implemented in current parity scaffold |

## WebSocket Flows

| Host/path | Flow | Confidence | Current .NET status |
| --- | --- | --- | --- |
| `api-socket.jibo.com/{token}` | token-authenticated socket for API-side signaling | medium | stub endpoint implemented |
| `neo-hub.jibo.com/{listen-path}` | listen turn flow with JSON and binary audio traffic | medium | fixture-backed synthetic turn flow implemented for `LISTEN`, `CONTEXT`, `CLIENT_NLU`, `CLIENT_ASR`, `EOS`, and first chat/joke skill responses |
| `neo-hub.jibo.com/v1/proactive` | proactive connection flow | medium | stub endpoint implemented |

### Current WebSocket Parity Slice

The current .NET pass covers only a narrow, explicitly synthetic subset of observed Neo-Hub behavior:

- token/session tracking across websocket turns
- explicit per-turn state tracking for transID, rules, context, buffered audio, and finalize attempts
- buffered audio accounting and turn-pending state
- auto-finalize triggering for raw audio once `LISTEN`, `CONTEXT`, and minimum buffered-audio thresholds are present
- `LISTEN` message handling with synthetic `LISTEN` result payload shaping
- `CONTEXT` capture for turn/session state
- `CLIENT_NLU` turn completion using remembered listen/session metadata
- `CLIENT_ASR` turn completion, including a synthetic STT seam for buffered-audio replay
- `EOS` emission after completed turns
- delayed `SKILL_ACTION` emission after `EOS` on completed turn flows to better match the Node oracle timing
- first richer vertical slice for joke/chat `SKILL_ACTION` playback

This does not yet mean parity for:

- real binary audio buffering and finalization
- real STT provider integration and external ASR lifecycle timing
- early-EOS behavior
- multi-step skill lifecycles beyond the current synthetic playback response
- broader interaction, animation, or ESML command families

Current raw-audio fallback behavior remains explicitly synthetic:

- when a buffered-audio turn can be resolved through the synthetic transcript-hint seam, `.NET` now auto-finalizes and emits `LISTEN` + `EOS` + `SKILL_ACTION`
- when the turn crosses the finalize threshold without a usable transcript, `.NET` now emits a fallback `LISTEN` + `EOS` + generic `SKILL_ACTION` rather than leaving the robot hanging on an unfinished turn
- that fallback is a compatibility measure inspired by the Node oracle, not a claim of real ASR understanding

### Internal ASR Direction

The current .NET websocket layer now separates:

- robot-facing websocket compatibility
- long-lived cloud session state
- per-turn websocket state
- transcript resolution / STT selection
- turn-to-response mapping

That separation is intentional. The synthetic STT path currently exists only to support fixture-driven replay while parity work continues. It should be treated as an internal compatibility seam, not as the final production ASR design.

## Upload Paths

| Path | Purpose | Confidence | Current .NET status |
| --- | --- | --- | --- |
| `/upload/asr-binary` | async audio/log upload target | medium | placeholder endpoint accepted |
| `/upload/log-events` | async log upload target | medium | placeholder endpoint accepted |
| `/upload/log-binary` | async binary upload target | medium | placeholder endpoint accepted |

## First Live .NET Capture Findings

The first real `.NET` robot run has confirmed only an early startup slice so far:

- `api.jibo.com` startup HTTP requests are reaching the `.NET` cloud
- `Notification.NewRobotToken` is active in the robot startup sequence
- `api-socket.jibo.com/{token}` is being accepted live

The first live run has not yet shown full startup parity with the working Node server. In particular, the successful Node run continues into additional health/log cadence after token issuance and socket acceptance, while the current `.NET` run has not yet reproduced that full progression consistently.

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

Sanitized fixtures live under [src/Jibo.Cloud/node/fixtures](/OpenJibo/src/Jibo.Cloud/node/fixtures) and should be expanded as real traffic is captured.
