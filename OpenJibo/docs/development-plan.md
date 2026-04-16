# Development Plan

## Summary

This document is the working implementation plan after the initial hosted-cloud scaffold.

It is intentionally broader than the current Node server. The Node server is a protocol oracle and discovery tool, not the complete map of Jibo.

## Current Scope

- stable .NET cloud scaffold
- Azure-oriented architecture and data ownership
- normalized runtime contracts for cloud-to-runtime handoff
- bootstrap documentation for region injection and targeted device patching
- starter endpoint coverage for account, notification, robot, loop, update, uploads, and core WebSocket acceptance
- starter xUnit coverage for the .NET application layer

## Next Implementation Scope

- expand HTTP `X-Amz-Target` coverage from observed traffic and fixtures
- grow WebSocket compatibility from stub acceptance into realistic turn orchestration
- keep websocket parity fixture-driven, starting with exact sequencing and payload-shape fidelity for the successful joke vertical slice before claiming broader skill coverage
- replace in-memory state with Azure SQL-backed persistence
- add structured fixture replay tests
- harden region/bootstrap docs by software version

## Discovery Scope

We still need to map more than the current Node server expresses. Priority discovery areas:

- all hostnames and service prefixes observed in real startup and turn traffic
- skill launch and skill lifecycle flows
- interactivity command families beyond the current joke flow
- richer embodied speech and animation behaviors
- upload, logging, backup, and key-sharing flows
- per-version configuration differences and region handling

## Current WebSocket Discovery Focus

The next fixture-driven websocket work should continue to separate three buckets:

- discovered behavior
  Grounded by the Node oracle, sanitized fixtures, and live captures
- implemented parity
  Only the narrow slices currently replayed and tested in `.NET`
- future hypotheses
  Ideas to investigate later, but not behaviors to silently bake into the hosted cloud

Right now the strongest implemented vertical slice beyond basic listen completion is the successful joke turn:

- `CLIENT_ASR` transcript-carrying turn completion
- synthetic `LISTEN` result shaping
- `EOS`
- delayed joke `SKILL_ACTION`

That should remain the model for future websocket work: capture first, fixture second, parity third.

The latest live captures also support a second discovery track:

- menu-driven `CLIENT_NLU` parity for clock, timer, and alarm flows
- richer transcript-bearing `CLIENT_ASR` discovery beyond jokes
- buffered-audio preservation for eventual real ASR in `.NET`

Near-term ASR work should stay staged:

1. preserve and replay the websocket audio payloads honestly
2. validate a local tool-based decode/transcribe loop in `.NET`
3. compare that against Azure-hosted STT before choosing a default production path

That keeps Node as the reverse-engineering oracle while letting the long-term `.NET` cloud gain real STT seams without pretending they are finished.

## Speech, Animation, And ESML

The current joke flow is only a small foothold into Jibo expressiveness.

Future work should map:

- direct speech modifiers
- animation selection and filtering
- embodied speech behaviors
- ESML and SSML subsets
- interactions between speech, visuals, and timing

Useful external references:

- [Speak-Tweak Docs](https://hri2024.jibo.media.mit.edu/Speak-Tweak-Docs)
- [ESML PDF](https://hri2024.jibo.media.mit.edu/attachments/SDK-SDK---ESML-121023-203758.pdf)

## Future Scope

- full endpoint inventory beyond the current Node mapping
- OTA-driven recovery
- paid hosted plans or donation-supported hosting
- deeper on-device bridge and OS modernization
- more capable skill/runtime integration
- possible LLM or tool-use patterns inspired by workshop-era experimentation

## MCP-Like Ideas

Recent MIT workshop materials suggest experimentation around modern AI tooling for Jibo, including an MCP-oriented idea. We should treat that as inspiration for future OpenJibo directions, not as a present dependency or supported integration.
