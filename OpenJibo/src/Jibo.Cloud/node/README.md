# Jibo.Cloud.Node

## Role

This folder contains the protocol oracle for OpenJibo.

The Node server is still the best place to:

- observe how a real Jibo talks to the cloud
- discover endpoints, payloads, headers, and timing
- validate hypotheses quickly against hardware
- produce sanitized fixtures for the .NET port

It is no longer the intended production runtime.

## What Stays Here

- reverse-engineering work
- protocol discovery
- capture and replay fixture generation
- narrow experiments needed to unblock the .NET hosted cloud

## What Moves Out

- production hosting concerns
- long-term storage and deployment architecture
- hardened runtime orchestration
- Azure-facing operational concerns

Those belong in `../dotnet`.

## Current Capabilities

The prototype already demonstrates a meaningful slice of the old cloud:

- HTTPS API routing by `X-Amz-Target`
- WebSocket handling for Jibo communication paths
- token and session bootstrapping
- account, loop, robot, key, media, and update operations
- ASR-oriented audio handling with `ffmpeg` and `whisper.cpp`
- synthetic turn handling for greetings, time, jokes, and basic chat
- extensive request and WebSocket logging for protocol discovery

## Fixture Workflow

Use this implementation as the source of truth for replay fixtures.

- capture observed request and response pairs
- sanitize account ids, emails, tokens, hostnames, and secrets
- save fixtures under [fixtures](C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/node/fixtures)
- use those fixtures to drive the .NET compatibility port

## Real Device Reality

Today, a real Jibo still needs a controlled environment to talk to this server.

That means:

- controlled router or DNS interception
- redirection of legacy Jibo hosts
- RCM/device modification for TLS or host validation where required

That reality is documented in [device-bootstrap.md](C:/Projects/JiboExperiments/OpenJibo/docs/device-bootstrap.md). OTA is a future improvement path, not the current bootstrap dependency.

## Next Job

The main job of this folder now is to keep the .NET port honest.
