# Development Plan

## Summary

This document is the working implementation plan after the initial hosted-cloud scaffold.

It is intentionally broader than the current Node server. The Node server is a protocol oracle and discovery tool, not the complete map of Jibo.

Day-to-day feature sequencing now lives in [feature-backlog.md](/C:/Projects/JiboExperiments/OpenJibo/docs/feature-backlog.md).

Cloud release hygiene:

- keep a visible OpenJibo Cloud version string
- expose it through diagnostics such as `/health` and the spoken `cloud version` command
- bump the shared version constant whenever we deploy a meaningful hosted-cloud change

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

## Latest Capture Findings

The latest live test round tightened up three priorities:

- yes/no turns need explicit constrained follow-up handling instead of generic chat routing
- skill invocation still depends too much on narrow phrase matching and is vulnerable to STT drift
- local buffered-audio STT in `.NET` is useful for discovery, but it is not yet stable enough to be the default live-test assumption

Evidence from the latest `2026-04-18` captures:

- several buffered-audio turns never produced a usable transcript because the local `whisper.cpp` path was missing or the temporary normalized Ogg file was rejected by `ffmpeg`
- some recognized phrases fell into placeholder provider replies because the intent was recognized but the feature path behind it is still a stub
- short yes/no responses need the same session-aware treatment already prototyped in Node, especially for create-flow style follow-ups

Evidence from the latest word-of-the-day capture round:

- yes/no photo confirmation improved and now completes through the constrained follow-up path
- `CLIENT_NLU` menu navigation is surfacing richer `destination` entities such as `snapshot`, `fun`, and `word-of-the-day`
- word-of-the-day guesses can arrive as structured `CLIENT_NLU` turns with `intent=guess`, `rules=["word-of-the-day/puzzle"]`, and `entities.guess=<word>`
- those structured turns should be treated as first-class cloud inputs even when no free-form transcript is present

Evidence from the continued `2026-04-18` word-of-the-day and time captures:

- spoken "start word of the day" style requests should route into the same word-of-the-day launch path as the menu destination
- spoken puzzle answers like `pastoral` should be treated as valid guesses whenever the active listen rules show `word-of-the-day/puzzle`
- spoken numeric line picks like `two` should resolve through the active word-of-the-day hint order instead of being treated as generic chat
- after a successful word-of-the-day completion, late empty same-turn audio should be ignored instead of generating a stale blank-audio follow-up
- post-game hotphrase blank-audio turns should be treated as cleanup noise, not a new cloud conversation turn
- clock replies should use the user-facing hour format without a leading zero

Evidence from the smaller `2026-04-18/19` hotphrase and word-of-the-day verification bundle:

- hotphrase silence can still auto-finalize into a generic `heyJibo` fallback, which sounds confused on-robot compared with a dedicated greeting path
- voice-triggered `loadMenu + destination=word-of-the-day` reaches Nimbus successfully, but Nimbus still expects a follow-up cloud skill response and times out if launch stops at `LISTEN` + `EOS`
- the newer `jibo test 2` bundle shows voice launch now reaches Nimbus and receives a cloud response, but a generic `SLIM/RUNTIME_PROMPT` just says "starting word of the day" instead of performing the menu-style redirect the on-screen path uses
- the `jibo test 3` bundle confirms Nimbus rejects `REDIRECT` in that cloud-skill slot, so the better next experiment is to hint the on-robot target skill directly on the synthetic `LISTEN` result and skip Nimbus `SKILL_ACTION` entirely for word-of-the-day launch
- the same bundle also shows `word-of-the-day/right_word` cleanup turns need a short ignore window for trailing audio or the robot can stay stuck in a blue-ring listening state
- the `jibo test 4` bundle exposed a broader websocket issue: inbound robot `LISTEN` setup packets were still being routed through turn finalization instead of just priming pending state, which can corrupt menu and word-of-the-day flows by treating setup turns like resolved intents
- the `jibo test 5` bundle suggests the remaining WOD launch and post-win cleanup bugs share the same root cause: we were leaving the robot-side `cloudSkillResponse` promise unresolved on `word_of_the_day`, `word_of_the_day_guess`, and `word-of-the-day/right_word`, so the latest .NET pass now emits a completion-only silent `SKILL_ACTION` for those paths instead of stopping at `LISTEN` + `EOS` or going fully silent
- the `jibo test 6` bundle plus the attached `@be` source snapshot refine that diagnosis: Nimbus does accept the silent completion response, but treats it as a normal `SLIM/RUNTIME_PROMPT` instead of a skill redirect, while the successful on-robot path is built around `menu + domain=word-of-the-day` skill switching through `SkillSwitchScheduler`
- the attached `be-framework.js` adds one more strong clue: the Be relaunch hook reads `skillData.nlu.skill`, so synthetic cloud launch turns for word-of-the-day should carry the explicit target skill name in the outbound NLU payload instead of expecting the robot to infer it from `intent/domain` alone
- the `JiboOs/V3.1` Nimbus source confirms the hotphrase/global launch path still routes through `@be/nimbus` and waits on `listenResult.cloudSkillResponse`, while Nimbus only supports a narrow set of cloud JCP behaviors and does not use cloud `REDIRECT` to jump into local skills; by contrast, the post-win `word-of-the-day/right_word` turn is a local `Optional-Response`, so the cleaner robot-side closeout is to synthesize an immediate empty `LISTEN + EOS` no-response result rather than replying with only `SKILL_ACTION`
- the same `jibo test 6` capture also shows the blue-ring cleanup loop was partly self-inflicted in `.NET`: after `word-of-the-day/right_word` we stopped the active turn, but later stray binary audio on the same transID could still re-arm buffering even without a fresh `LISTEN`, so the next pass now requires a real listen phase before post-turn audio can reopen buffered completion
- the local buffered-audio seam is still producing repeated `whisper.cpp returned no transcript` and `ffmpeg ... Codec not found` failures, so lightweight waveform or energy screening is worth considering once the core launch flow is stable

Near-term interaction work should now prioritize:

1. preserve and interpret yes/no turn constraints from observed listen rules
2. broaden phrase-to-intent matching for the small set of known working skills before moving to larger NLU ambitions
3. keep synthetic transcript hints as the most reliable parity path when captures already provide them
4. continue evaluating whether local preprocessing is worth further investment or whether managed STT should replace it for the next serious testing phase
5. start separating laptop-local capture storage from the eventual hosted retention/export path so group testing does not depend on repo-local zip handling

## Capture Storage Direction

Repo-local NDJSON plus zipped capture bundles are still good enough for current reverse-engineering and single-operator testing.

For hosted group testing, the next direction should be:

1. keep local file sinks for dev and laptop workflows
2. add a cleaner export/archive boundary so noteworthy sessions can be promoted without copying raw capture trees around manually
3. plan for hosted durable storage separately from the runtime node that is serving live robot traffic
4. keep fixture generation and sanitized replay artifacts as the stable handoff format between local testing and hosted debugging

## Working Cloud Framework

The current evidence in captures, fixtures, and Node behavior supports three main cloud interaction paths:

1. local Jibo behavior observed by the cloud
   The robot or its local skill stack already interpreted the turn and the cloud mainly tracks, acknowledges, or lightly completes it.
2. local Jibo behavior overridden or redirected by the cloud
   The robot reports the turn state, but the cloud chooses a different synthetic reply path.
3. raw audio interpreted by the cloud
   The robot sends buffered audio and the cloud performs transcript resolution before sending back `LISTEN`, `EOS`, and ESML-driven playback.

Those are the right primary buckets for now. Additional side channels may still emerge later, especially around proactive traffic, direct skill/service sockets, or future on-device OS changes, but they should be treated as extensions to this model until captures prove otherwise.

Latest stock-OS WOD findings:

- `word-of-the-day/right_word` closeout should not emit a synthetic `match`; otherwise Jetstream promotes it into `globalTurnResult` and Global Service relaunches Nimbus a few seconds later with a `Cloud Skill Response Timeout`.
- Voice `play word of the day` hotphrase launch still enters Global Service first, so a synthetic `LISTEN` result alone is not enough. The next-most-correct transport hint is a direct `SKILL_REDIRECT` event aimed at `@be/word-of-the-day`, alongside the menu-shaped `LISTEN` payload.
- Stock OS also keeps the original hotphrase/global launch cloud response promise alive even after the redirect succeeds, so voice WOD launch needs an explicit silent `SKILL_ACTION` completion on the same transID to avoid later cloud-response culling and an interrupted game state.
- Auto-dismissing `word-of-the-day/right_word` with a no-input `LISTEN`/`EOS` stops the listening ring, but it does not close the WOD UI by itself. Pairing that no-input closeout with an explicit redirect back to `@be/idle` is the current cleanest approximation.
- OTA/update yes-no prompts can advertise `$YESNO` only through ASR hints rather than `listenRules`, so short denials like `no` need to be recognized from `listenAsrHints` too.
- Spoken WOD guesses should preferentially snap to the closest offered hint when Whisper lands very close to one of the menu words, since near-misses like `haglet` for `aglet` are common in live testing.
- The stock robot still misroutes constrained local turns if the cloud echoes `globals/*` rules back on the reply. For spoken WOD guesses and settings/update `no`, we should only return the local rule (`word-of-the-day/puzzle`, `settings/download_now_later`, etc.) so Global Service does not relaunch Nimbus.

Latest radio discovery findings:

- `@be/radio` is a true local skill, not a cloud placeholder.
- Its `open(result, refresh, previousSkillName)` path treats `result.nlu.intent === "menu"` as a `play` launch.
- `result.nlu.entities.station` is the genre selector, and `Country` is a real supported station key from the robot's `genres.json`.
- The smallest stock-shaped cloud handoff for voice launch is therefore a local `SKILL_REDIRECT` to `@be/radio` with `nlu.intent = "menu"`, optional `entities.station`, and a silent completion to settle the hotphrase cloud response.

Latest news discovery findings:

- Nimbus explicitly treats `match.cloudSkill === "news"` like the GQA path and waits on `cloudSkillResponse`.
- The first OpenJibo news pass should therefore use a real cloud-skill shape, not a generic placeholder chat reply.
- For now, the content can stay synthetic while the protocol is grounded: `match.cloudSkill = "news"` plus a supported `SLIM` announcement response is enough to validate the robot path before provider-backed headlines arrive later.

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
