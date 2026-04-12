# Jibo.Cloud.DotNet

## Summary

`Jibo.Cloud.DotNet` is the stable hosted implementation of the OpenJibo cloud.

This is the production-oriented path for restoring device connectivity and creating a foundation for future runtime, AI, and OTA work.

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
- synthetic `LISTEN` result shaping for `LISTEN`, `CLIENT_NLU`, and `CLIENT_ASR`
- `CONTEXT` capture and follow-up turn state
- `EOS` completion
- first skill vertical for joke/chat `SKILL_ACTION` playback

Not yet covered:

- real binary audio / ASR finalization parity
- upstream Nimbus or broader skill lifecycle behavior
- animation / expression command families
- ESML feature parity beyond the narrow synthetic playback payloads used in the current scaffold
