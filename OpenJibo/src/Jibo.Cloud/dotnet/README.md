# Jibo.Cloud.DotNet

## Summary

`Jibo.Cloud.DotNet` is the stable hosted implementation of the OpenJibo cloud.

This is the production-oriented path for restoring device connectivity and creating a foundation for future runtime, AI, and OTA work.

Current spoken cloud version: `Open Jibo Cloud version 1.0.16.`

Release hygiene reminder:

- bump [OpenJiboCloudBuildInfo.cs](/C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs) whenever we ship a meaningful hosted-cloud update
- keep the spoken version response and `/health` version field aligned from that single source of truth
- the API startup log now prints the same version on boot, which is useful for confirming the running build during live robot tests

## Architecture

The first implementation is a modular monolith:

```text
Api -> Application -> Domain -> Infrastructure
```

This keeps deployment simple while preserving clean boundaries.

## Azure Direction

The target Azure footprint is:

- Azure App Service for HTTP and WebSocket traffic
- Azure SQL for relational persistence
- Azure Blob Storage for uploads and update artifacts
- Azure Key Vault for secrets and certificates
- Application Insights for observability

Azure SQL is the primary system of record for:

- accounts
- devices
- sessions
- update metadata
- host mappings
- bootstrap and provisioning records

## Compatibility Goal

The first compatibility milestone is `core revive`.

That means the .NET cloud should handle:

- token and session issuance
- account and robot identity flows needed for startup
- core `X-Amz-Target` dispatch
- listen and proactive WebSocket paths
- basic media and update metadata responses
- handoff into normalized `TurnContext` and `ResponsePlan` contracts

## Relationship To The Node Prototype

The Node server remains the discovery harness and fixture source.

The .NET implementation should:

- copy observed behavior where needed
- use fixtures captured from Node and real robots
- avoid speculative protocol design
- separate HTTP parity, websocket parity, and future discovery work so coverage stays honest

## Current State

This folder now contains the first hosted scaffold, not just a README.

The intent is to grow from a runnable dev monolith into the real Azure deployment target without abandoning the existing abstractions work.

Current websocket scope is still intentionally narrow:

- token-backed socket sessions
- explicit websocket turn-state tracking separate from long-lived cloud session state
- synthetic `LISTEN` result shaping for `LISTEN`, `CLIENT_NLU`, and `CLIENT_ASR`
- buffered audio state tracking behind a dedicated turn-finalization layer
- raw audio auto-finalization once `LISTEN` + `CONTEXT` + minimum buffered audio thresholds are present
- synthetic STT strategy selection for fixture-driven audio turn completion
- structured websocket telemetry and live-run fixture export
- `CONTEXT` capture and follow-up turn state
- `EOS` completion
- delayed `SKILL_ACTION` emission after `EOS` to preserve the current Node-observed turn sequence
- first skill vertical for joke/chat `SKILL_ACTION` playback
- repo-root live-run capture support for both `captures/http/` and `captures/websocket/`

Not yet covered:

- real binary audio / ASR finalization parity
- provider-backed ASR integration
- timed finalize/fallback behavior matching richer Node turn-state semantics
- upstream Nimbus or broader skill lifecycle behavior
- animation / expression command families
- ESML feature parity beyond the narrow synthetic playback payloads used in the current scaffold

## Live Capture Status

The first real `.NET` robot test has confirmed:

- startup HTTP traffic reaches the `.NET` cloud
- `Notification.NewRobotToken` is in the active startup path
- `api-socket.jibo.com` connections are being accepted live

It has not yet confirmed:

- full startup parity with the successful Node run cadence
- consistent eye-open / wake completion on the robot
- the later health/log upload sequence currently seen in the working Node run

Current raw-audio behavior is still a compatibility bridge:

- if buffered audio has a synthetic transcript hint, the server now auto-finalizes the turn and emits `LISTEN` + `EOS` + `SKILL_ACTION`
- if buffered audio crosses the finalize threshold without a usable transcript, the server now emits a Node-style fallback completion with `EOS` instead of hanging the turn forever
- this is intentionally not a claim of real ASR parity
- follow-up turns now preserve enough constraint state to distinguish yes/no-style replies from ordinary free-form chat
- create-flow yes/no turns now preserve `create/is_it_a_keeper` and `domain=create` in the outbound synthetic `LISTEN` payload
- structured word-of-the-day guesses now complete as `CLIENT_NLU` turns instead of falling back to pending/blank-audio behavior
- spoken word-of-the-day launch phrases now route into the same cloud intent as the on-screen menu path
- spoken word-of-the-day puzzle answers now emit menu-compatible `guess` turns, including line-number picks resolved through the observed hint order
- voice-triggered word-of-the-day launches now emit the same `loadMenu + destination=word-of-the-day` shape the robot already uses successfully from the menu
- hotphrase `[BLANK_AUDIO]` cleanup turns are ignored instead of reopening the cloud into a stale blank-audio comment path after word-of-the-day completion
- phrase matching has been widened slightly for known test prompts such as joke, dance, surprise, weather, calendar, commute, and news variants
- time replies now use the natural hour format without a leading zero
- plain time/date/day questions now travel through stock-shaped local `@be/clock` handoffs, and `open the clock` uses the direct clock-view path instead of the menu path
- timer/alarm voice launches now accept compact alarm forms like `830` and `8 30`, and malformed timer/alarm requests stay on a clarification reply instead of generic cloud chat
- media and update metadata now persist to a local state file so gallery/update behavior is not lost on every process restart

## Buffered Audio STT

The current `.NET` websocket stack now preserves buffered Ogg/Opus websocket frames in memory for each in-flight turn.

That enables two distinct STT paths:

- fixture-oriented synthetic transcript hints for replay and parity tests
- an opt-in local tool-based path that can normalize the buffered Ogg pages, call `ffmpeg`, and then call `whisper.cpp`

The local tool path is intentionally off by default. It exists to help map real robot audio behavior while the stable hosted cloud remains the primary goal.

For local Ubuntu testing, the checked-in API host config now enables that path by default with the current Node-aligned tool locations:

- `/usr/bin/ffmpeg`
- `/usr/bin/whisper.cpp/build/bin/whisper-cli`
- `/usr/bin/whisper.cpp/models/ggml-base.en.bin`
- temp audio under `/tmp/openjibo-stt`

Configuration lives under `OpenJibo:Stt`:

- `EnableLocalWhisperCpp`
- `FfmpegPath`
- `WhisperCliPath`
- `WhisperModelPath`
- `WhisperLanguage`
- `TempDirectory`

This is not yet a claim of production-ready onboard ASR. It is a `.NET` discovery seam that keeps us compatible with the Node oracle while we evaluate longer-term options such as Azure-hosted STT or a managed decode/transcribe stack.

Latest live-capture guidance after the `2026-04-18` round:

- prefer synthetic transcript hints when they are present in the observed turn
- only use local `whisper.cpp` when the configured tool paths are real and the decode chain is behaving
- treat `ffmpeg` decode failures on normalized Ogg captures as evidence that the local audio path still needs more hardening before it can be the default live-test expectation
- keep the Node implementation as the oracle for yes/no turn semantics and audio preprocessing details until the `.NET` port catches up

Capture-storage guidance while moving toward hosted group testing:

- repo-local file captures remain the default for laptop-based reverse engineering
- hosted deployments should keep runtime request handling decoupled from long-term capture retention
- sanitized fixtures remain the preferred durable artifact for parity work and bug reproduction

Current local state persistence:

- default path: `App_Data/cloud-state.json` under the running API directory
- current contents: media metadata, backup metadata, and staged update metadata
- current limitation: media bodies are only preserved through the existing text-based HTTP body capture seam, so this is a hosted-gallery bridge, not final binary-safe media storage

## Current Interaction Paths

The working cloud model currently looks like three main paths:

1. Jibo reports what already happened locally and the cloud tracks or lightly completes the turn.
2. Jibo reports what happened locally and the cloud responds with a different synthetic completion path.
3. Jibo streams raw audio and the cloud interprets the turn before sending ESML back.

That framing matches the repo evidence so far and is a good operating model for current discovery. There may still be smaller side paths around proactive traffic, direct skill-to-service communication, or future on-robot extensions, but those are not the main cloud revive loop yet.
