# Development Plan

## Summary

This document is the current working plan for the OpenJibo hosted cloud.

The production lane is the `.NET` cloud in `src/Jibo.Cloud/dotnet`. The Node server remains the protocol oracle, capture harness, and fast reverse-engineering lab, but it is no longer the long-term hosted architecture.

Day-to-day feature sequencing lives in [feature-backlog.md](feature-backlog.md). This file tracks release shape, current code truth, evidence sources, and the boundary between `1.0.18` closeout work and `1.0.19` follow-up work.

## Current Release Snapshot

- Current OpenJibo Cloud release constant: `1.0.18`
- Source of truth: [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- Spoken diagnostic: `Open Jibo Cloud version 1 dot 0 dot 18.`
- HTTP diagnostic: `/health` returns the same version
- Startup diagnostic: the API logs the same version on boot
- .NET target framework: `net10.0` across the cloud projects and cloud test project

Release `1.0.18` is now in feature-hardening. Its main bug-fix theme is alarm and photo/gallery behavior on stock OS `1.9`, with a few small feature slices added while the test loop is warm.

## Release Rhythm

This is the working pattern for each hosted-cloud release:

1. Pick a narrow source-backed feature or compatibility slice.
2. Confirm the stock payload shape from captures, Pegasus, the JiboOS reference tree, or live logs.
3. Implement the smallest `.NET` path that can be tested honestly.
4. Add focused tests around routing, websocket payload shape, and state behavior.
5. Run the stock robot live test, collect captures, and record the result before moving on.
6. Keep regressions and bug fixes in the current release; roll larger follow-up work into the next version.

For `1.0.18`, the remaining release work should stay small: finish one or two feature slices, run the live regression pass, and only patch bugs found in that pass before calling the version complete. `1.0.19` should then reopen the broader feature queue.

## Current Code Truth

The hosted `.NET` cloud is a modular monolith:

```text
Jibo.Cloud.Api -> Jibo.Cloud.Application -> Jibo.Cloud.Domain -> Jibo.Cloud.Infrastructure
```

Current API and protocol scope:

- HTTP `X-Amz-Target` dispatch through `JiboCloudProtocolService`
- `/health` diagnostics
- WebSocket acceptance for `api-socket.jibo.com`, `neo-hub.jibo.com` listen, and `neo-hub.jibo.com/v1/proactive`
- token/session issuance for account, hub, and robot startup flows
- starter account, notification, loop, media, key, person, backup, robot, update, and upload/log handling
- media lookup through `/media/{path}`
- no placeholder no-op update from `GetUpdateFrom` when no staged update exists

Current websocket scope:

- long-lived cloud session state separated from per-turn websocket state
- `LISTEN`, `CONTEXT`, `CLIENT_NLU`, `CLIENT_ASR`, and binary-audio handling
- pending listen setup packets kept pending instead of finalized as turns
- buffered Ogg/Opus audio preservation per turn
- synthetic transcript hint support for fixture-driven parity
- opt-in local `ffmpeg` plus `whisper.cpp` STT path for discovery
- auto-finalize thresholds for buffered audio after a real listen phase
- late-audio ignore windows after completed turns
- no-input local completion for constrained prompts
- unknown inbound websocket types dropped silently instead of echoing stock-OS-unknown OpenJibo events
- file telemetry and fixture export for HTTP, websocket, and turn captures

Current state and persistence scope:

- `InMemoryCloudStateStore` remains the runtime store
- a local JSON persistence bridge is enabled by default at `App_Data/cloud-state.json`
- persisted state currently covers staged updates, media metadata, and backup metadata
- this is a bridge toward Azure SQL and Blob Storage, not the final hosted storage architecture

## Implemented In Current `1.0.18` Source

The following behavior is present in source and covered by focused tests:

- `cloud version` speech and `/health` version reporting share `OpenJiboCloudBuildInfo.Version`
- apostrophes are no longer escaped to `&apos;` in spoken ESML, while `&`, `<`, `>`, and `"` remain escaped
- radio voice launch supports `open the radio` and genre launch such as `play country music`, using local `@be/radio` `menu` payloads, `SKILL_REDIRECT`, and silent completion
- news has a first Nimbus-shaped cloud path using `match.cloudSkill = news` and a `news` `SKILL_ACTION` with synthetic briefing content
- stock-shaped clock handoffs cover time, date, day, clock open, timer/alarm menu, timer/alarm value, timer/alarm clarification, and timer/alarm delete
- alarm parsing covers forms such as `7:30 am`, `830`, `8 30`, `10-25`, `10:25 pm`, and `10 25 p m`
- ambiguous alarm times can prefer the next local occurrence when the robot context includes `runtime.location.iso`
- `CLIENT_NLU intent=set` with only `domain=alarm` stays on the local clock clarification path instead of defaulting to a fabricated time
- `CLIENT_NLU intent=cancel` on `clock/alarm_timer_query_menu` can reuse the last active clock domain
- photo flows route `open photo gallery` to `@be/gallery`, `snap a picture` to `@be/create/createOnePhoto`, and `open photobooth` to `@be/create/createSomePhotos`
- passive gallery/create context does not reopen a stale cloud turn
- media metadata persists across store recreation and `/media/{path}` can serve the current text-body placeholder payload
- constrained yes/no handling covers `create/is_it_a_keeper`, `shared/yes_no`, `settings/download_now_later`, `surprises-date/offer_date_fact`, `surprises-ota/want_to_download_now`, and `$YESNO` hints
- outbound constrained yes/no responses strip unrelated `globals/*` rules so stock OS stays local
- no-input fallback for constrained yes/no prompts emits local `LISTEN`/`EOS` instead of relaunching generic Nimbus speech
- Word of the Day launch, spoken guesses, structured `CLIENT_NLU` guesses, hint-order guesses, fuzzy hint matching, right-word cleanup, and late audio cleanup are covered in the websocket layer

## Reference Sources

Use these sources as evidence, not as code to copy blindly:

- OpenJibo Node oracle: [open-jibo-link.js](../src/Jibo.Cloud/node/open-jibo-link.js)
- Current hosted `.NET` cloud: [src/Jibo.Cloud/dotnet](../src/Jibo.Cloud/dotnet)
- Live captures and robot logs: `.\artifact-output`
- Original Pegasus cloud source: `..\jibo\pegasus`
- Original SDK and skill source snapshot: `..\jibo\sdk`
- JiboOS reference tree: `..\JiboOS`
- JiboOS skill snapshot: `..\JiboOS\opt\jibo\Jibo\Skills\@be`

The Pegasus tree is especially useful for cloud service intent: `packages/hub` documents `/v1/listen`, `/nlu`, and `/asr`; `packages/lasso` documents credential and provider aggregation; `packages/history` and the architecture materials are useful for future memory and proactivity work.

The JiboOS trees are especially useful for local skill ownership and payload shape: `@be/clock`, `@be/gallery`, `@be/create`, `@be/radio`, `@be/nimbus`, `@be/settings`, `@be/surprises*`, `@be/restore`, `@be/who-am-i`, and `@be/idle`.

When sources disagree, prefer the newest live stock-OS capture for runtime behavior, then stock robot source for local ownership, then Pegasus for original cloud intent, then Node for known working compatibility behavior.

## `1.0.18` Closeout Gates

Before calling `1.0.18` complete, prove or explicitly defer these:

- Run the focused `.NET` cloud test suite after the last feature slice.
- Confirm the running robot build reports cloud version `1.0.18`.
- Regression test alarm flows: set with explicit time, set with compact/spoken time, clarify missing time, cancel alarm, and local cleanup prompts.
- Regression test photo/gallery flows: open gallery, answer the stock `shared/yes_no` prompt, hand into create, take one photo, and avoid blue-ring stale turns.
- Live-test radio launch: `open the radio` and `play country music`.
- Live-test first news path: `tell me the news` should use the Nimbus cloud-skill lane instead of generic chat.
- Recheck constrained yes/no prompts for update/backup/share/gallery without leaking global rules.
- Recheck that stock OS no longer logs OpenJibo-only websocket events such as synthetic pending/context/ack packets from the current build.
- Treat remaining `ffmpeg` / `whisper.cpp` transcript failures as STT work unless the capture proves a separate turn-routing regression.

## Known Gaps

These are not blockers for calling `1.0.18` complete unless the live test shows a regression in a current release path:

- local `whisper.cpp` STT remains a discovery seam, not production ASR
- media upload/body handling is not binary-safe enough for final gallery originals and thumbnails
- state persistence is local JSON, not Azure SQL / Blob Storage
- update, backup, and restore are not end-to-end proven
- news content is synthetic
- weather, calendar, commute, personal report, identity, memory, and proactivity are still mostly discovery or placeholder content paths
- volume, stop, robot age, and command-versus-question personality routing are not implemented yet

## `1.0.19` Direction

After `1.0.18` is tested and tagged, `1.0.19` should move back into feature work:

- one lightweight device-control feature, most likely stop or volume
- end-to-end update/backup/restore proof
- STT reliability improvements, including noise screening and a managed STT comparison
- provider-backed first content path, likely news or weather
- hosted capture/export boundary for group testing
- continued Pegasus/JiboOS-backed mapping for proactivity, memory/history, Lasso-style aggregation, and identity

## Azure Direction

The target hosted footprint remains:

- Azure App Service for HTTP and WebSocket traffic
- Azure SQL for accounts, devices, sessions, host mappings, updates, media metadata, and provisioning records
- Azure Blob Storage for media bodies, upload artifacts, update payloads, and curated capture bundles
- Azure Key Vault for secrets and certificates
- Application Insights for diagnostics and live-test observability

Local JSON persistence is only a stepping stone. Do not design new feature slices as if local file state were the final hosted store.
