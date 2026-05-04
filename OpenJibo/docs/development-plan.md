# Development Plan

## Summary

This document is the current working plan for the OpenJibo hosted cloud.

The production lane is the `.NET` cloud in `src/Jibo.Cloud/dotnet`. The Node server remains the protocol oracle, capture harness, and fast reverse-engineering lab, but it is no longer the long-term hosted architecture.

Day-to-day feature sequencing lives in [feature-backlog.md](feature-backlog.md). Live closeout checks live in [regression-test-plan.md](regression-test-plan.md). This file tracks release shape, current code truth, evidence sources, and the boundary between `1.0.18` closeout work and `1.0.19` follow-up work.

## Current Release Snapshot

- Current OpenJibo Cloud release constant: `1.0.18`
- Source of truth: [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- Spoken diagnostic: `Open Jibo Cloud version 1 dot 0 dot 18.`
- HTTP diagnostic: `/health` returns the same version
- Startup diagnostic: the API logs the same version on boot
- .NET target framework: `net10.0` across the cloud projects and cloud test project

Release `1.0.18` is now in feature-hardening. Its main bug-fix theme is alarm and photo/gallery behavior on stock OS `1.9`, with a few small feature slices added while the test loop is warm.

## Latest Live Evidence

`jibo test 30` confirmed the cloud-version self-hotphrase fix and exposed two remaining stock-skill wrinkles: local gallery/backup proactivity after an empty-gallery prompt, and a duplicate clock relaunch after an alarm value follow-up.

- Before the cloud-version test, the robot's local `jibo-server-service` restarted after a broken pipe, then `ssm` raised `Q4-Server_connection_lost` and local `@be/settings` opened the connection-lost error path. The notification connection recovered about 31 seconds later. Treat early-test confusion as suspect if this local-server recovery appears in the same window.
- The cloud-version answer itself proved the running build was `1.0.18`, but the previous source treated `cloud_version` as a follow-up conversation. A fresh hotphrase `LISTEN` then captured speech tail as `Cloudford.`, and generic chat replied `thanks. I heard, Cloudford.`
- Current source now makes `cloud_version` a one-shot diagnostic, uses a longer diagnostic speech-tail ignore window, and ignores no-transcript hotphrase launch `LISTEN` setup packets inside that window. The existing no-`LISTEN` binary guard already ignored same-transID binary tails after finalization, but Test 27 showed it could not stop a brand-new hotphrase listen by itself.
- Test 28 showed our cloud-version/generic Nimbus `LISTEN` match entering stock BE with `skipSurprises` unset. After Nimbus settled, BE requested local `@be/surprises`; Test 28 inhibited the offer because VAD heard people talking, while Test 27 used the same doorway to select `@be/surprises-ota` and speak the backup-in-progress warning.
- Current source now emits `match.skipSurprises = true` for hosted turn results, fallback matches, and local skill redirects. Stock BE maps that to `skipSurprisesExternal`, preventing normal cloud replies from falling into end-of-skill surprises such as OTA/backup prompts.
- Test 29 showed the deployed `skipSurprises` payload in the robot logs and did not produce another backup announcement in the focused run. It still interrupted cloud-version speech because the spoken phrase `Open Jibo Cloud version...` included `Jibo`; stock Nimbus runs the response as a runtime MIM, and the local hotphrase detector stopped TTS before our cloud-side late-listen ignore could help.
- Current source now speaks the diagnostic as `Cloud version ...` without saying `Jibo`, while keeping the one-shot and late-listen cleanup guards.
- Test 30 showed `cloud version` speaking cleanly with no interruption. The backup warning later appeared after opening gallery from the menu: gallery asked the empty-gallery photo question, then stock BE opened `@be/surprises`, selected `@be/surprises-ota`, and spoke the local backup announcement. The captured HTTP traffic still did not show hosted `Backup_*` calls.
- Test 31 sharpened the remaining alarm/back-up picture: the startup capture includes a legacy `Backup_20170222.List` request before any voice turn, the alarm set path still collapsed `7:11 AM` into `7:00 PM` / `setting alarm for seven`, and the later clock `No` replied `that's fine` before the robot opened `@be/surprises` and eventually got stuck in a blue-ring listen loop until reset.
- Test 32 shows the alarm set path is better, but two cleanup gaps remain in the newer-code window: the alarm flow can still leave a listen open at the end, and the proactive Word of the Day yes/no branch can miss a short `Yes` and bounce into a mock/echo response. The delete-alarm retry case also still asks whether to set an alarm again, then mishandles the follow-up yes/no reply.
- The websocket turn telemetry now emits compact snapshots for `binary_audio_received`, `binary_audio_ignored`, `yes_no_turn_received`, `yes_no_turn_resolved`, and `yes_no_no_input`, so the next live pass can prove whether the yes/no rule survived buffering and finalization.
- Test 30 showed the alarm value reply `638` arrived at 6:38:13 AM local. Stock clock parsed that as `6:38 PM`, and our cloud response then added a delayed `@be/clock` relaunch on top of the active local clock value flow, causing the duplicate existing-alarm replacement prompt. Current source now suppresses the extra clock relaunch for local clock follow-up rules.
- Backup-in-progress still appears robot-local in the user-facing voice flow. Tests 27, 28, 29, and 30 had no matching `Backup_*` HTTP calls during the voice prompt itself. Keep investigating robot-local scheduler/status, startup reconnect state, CPU/load, and log/upload work if backup status itself remains sluggish after surprise suppression.
- Test 26 remains the broader regression evidence for gallery success, alarm replacement/delete risk, stop/volume live proof, and short-answer STT weakness. Alarm replacement/menu agreement is still a live release risk, but Test 30 identified and patched one duplicate-handoff cause.

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
- local whisper only attempts external decoding when buffered audio contains an Opus identification header
- auto-finalize thresholds for buffered audio after a real listen phase
- late-audio ignore windows after completed turns
- cloud-version diagnostic turns do not keep follow-up open and receive a longer speech-tail ignore window
- no-transcript hotphrase launch `LISTEN` setup packets are ignored while a completed diagnostic/local turn is still in its late-audio cleanup window
- passive local context cleanup for gallery/create/settings contexts after stock local skills take ownership
- no-input local completion for constrained prompts, clock value prompts, gallery preview prompts, and settings volume-control prompts
- active local prompt preservation so `shared/yes_no`, clock, gallery, and settings prompts can still consume transcript-bearing short replies even when the stock skill reports a local context
- binary audio ignored for an existing transID until a fresh `LISTEN` has been seen, preventing context-only or post-speech tails from reopening an endless buffered turn
- blank-audio hotphrase turns clear pending listen state and install a short late-audio ignore window
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
- `cloud version` is a one-shot diagnostic: it speaks the version without opening a follow-up turn, then shields the speech tail from self-listen artifacts such as the Test 27 `Cloudford.` capture
- the spoken cloud-version diagnostic avoids saying `Jibo`, because Test 29 showed the prior `Open Jibo Cloud version...` wording could trigger local hotphrase barge-in during Nimbus TTS
- hosted turn results, fallback matches, and local skill redirects now emit `match.skipSurprises = true` so stock BE does not route settled cloud/local responses into `@be/surprises`
- apostrophes are no longer escaped to `&apos;` in spoken ESML, while `&`, `<`, `>`, and `"` remain escaped
- radio voice launch supports `open the radio` and genre launch such as `play country music`, using local `@be/radio` `menu` payloads, `SKILL_REDIRECT`, and silent completion
- news has a first Nimbus-shaped cloud path using `match.cloudSkill = news` and a `news` `SKILL_ACTION` with synthetic briefing content
- stop commands such as `stop that` and `never mind` emit stock `global_commands` `stop` NLU plus a local `@be/idle` redirect, without generic chat speech
- stop and cancel phrase matching tolerates stock ASR punctuation such as `Never mind.`
- volume commands emit stock `global_commands` volume intents: `volumeUp`, `volumeDown`, and `volumeToValue` with `volumeLevel`; `show volume controls` redirects to `@be/settings` `volumeQuery`
- volume-to-value parsing handles the observed stock ASR homophone shape `Set Volume 2-6.` as level `6`
- stock-shaped clock handoffs cover time, date, day, clock open, timer/alarm menu, timer/alarm value, timer/alarm clarification, and timer/alarm delete
- alarm delete parsing handles `delete the alarm` plus the observed stock ASR mishears `delete along` / `delete the along`
- clock delete/cancel handoffs do not keep a generic chat follow-up mic open or emit extra cloud speech after the local clock redirect
- alarm parsing covers forms such as `7:30 am`, `830`, `8 30`, `7, 44`, `10-25`, `10:25 pm`, and `10 25 p m`
- ambiguous alarm times can prefer the next local occurrence when the robot context includes `runtime.location.iso`
- short clock value follow-up transcripts are accepted under `clock/alarm_set_value` and `clock/timer_set_value` instead of being dropped before parsing
- local clock follow-up rules return normalized `LISTEN`/`EOS` without adding a second delayed `@be/clock` relaunch after the active stock clock skill has already consumed the reply
- `CLIENT_NLU intent=set` with only `domain=alarm` stays on the local clock clarification path instead of defaulting to a fabricated time
- `CLIENT_NLU intent=cancel` on `clock/alarm_timer_query_menu` can reuse the last active clock domain
- `CLIENT_NLU intent=cancel` on `clock/alarm_set_value` / `clock/timer_set_value` maps to local clock `cancel` instead of re-asking for a value
- photo flows route `open photo gallery`, observed `open photogal`, `snap a picture`, and `open photobooth` to the matching gallery/create local skills
- passive gallery/create/settings context does not reopen a stale cloud turn
- active local prompts under gallery/settings context are preserved so short `yes`/`no` answers can finalize the prompt instead of being suppressed as passive context
- media metadata persists across store recreation and `/media/{path}` can serve the current text-body placeholder payload
- constrained yes/no handling covers `clock/alarm_timer_change`, `clock/alarm_timer_none_set`, `create/is_it_a_keeper`, `shared/yes_no`, `settings/download_now_later`, `surprises-date/offer_date_fact`, `surprises-ota/want_to_download_now`, and `$YESNO` hints
- outbound constrained yes/no responses strip unrelated `globals/*` rules so stock OS stays local
- no-input fallback for constrained yes/no prompts emits local `LISTEN`/`EOS` instead of relaunching generic Nimbus speech, including `shared/yes_no` after STT failure
- no-input fallback for clock value prompts, `gallery/gallery_preview`, and `settings/volume_control` emits local `LISTEN`/`EOS` instead of generic `I heard you` Nimbus speech
- repeated empty `create/is_it_a_keeper` replies redirect to `@be/idle` after the second miss so the photo/create flow can settle instead of leaving a stale listening state
- local whisper skips buffered audio turns that do not contain `OpusHead`, preventing a known `ffmpeg` failure path from becoming the noisy failure mode
- Word of the Day launch, spoken guesses, structured `CLIENT_NLU` guesses, hint-order guesses, fuzzy hint matching, right-word cleanup, and late audio cleanup are covered in the websocket layer

## Reference Sources

Use these sources as evidence, not as code to copy blindly:

- OpenJibo Node oracle: [open-jibo-link.js](../src/Jibo.Cloud/node/open-jibo-link.js)
- Current hosted `.NET` cloud: [src/Jibo.Cloud/dotnet](../src/Jibo.Cloud/dotnet)
- Live captures and robot logs: `.\artifact-output`
- User-provided original source snapshot: `..\jibo` when extracted locally
- Original Pegasus cloud source inside that snapshot: `pegasus`
- Original SDK and skill source inside that snapshot: `sdk`
- JiboOS reference tree: `..\JiboOS`
- JiboOS skill snapshot: `..\JiboOS\opt\jibo\Jibo\Skills\@be`

The Pegasus tree is especially useful for cloud service intent: `packages/hub` documents `/v1/listen`, `/nlu`, and `/asr`; `packages/lasso` documents credential and provider aggregation; `packages/history` and the architecture materials are useful for future memory and proactivity work.

The JiboOS trees are especially useful for local skill ownership and payload shape: `@be/clock`, `@be/gallery`, `@be/create`, `@be/radio`, `@be/nimbus`, `@be/settings`, `@be/surprises*`, `@be/restore`, `@be/who-am-i`, and `@be/idle`.

The original test suites are useful as behavior contracts before more live-device trial and error:

- `..\jibo\sdk\skills\clock\tests\AlarmTimer` documents alarm/timer state expectations. Cancel at the alarm value prompt exits without scheduling; no-alarm query `yes` redirects to the value prompt while `no` exits without touching KB/scheduler; existing-alarm `keep` preserves KB/scheduler while `delete`, `change`, and `cancel` clear it; cross-domain cancel uses the `OtherSet` yes/no branch before deleting the other clock domain.
- `..\jibo\sdk\skills\gallery\tests` documents gallery ownership. Empty gallery `yes` redirects to `@be/create`, empty gallery `no` exits, media-load failure exits, gallery/item views lifecycle out around two minutes, and delete confirmation only deletes on a positive `yes`.
- `..\jibo\sdk\skills\surprises-ota\tests\OTASurprise.test.js` shows OTA/backup surprise priority is robot-local and rate-limited by status plus last-notification timestamps. Backup-in-progress sluggishness should be investigated as local scheduler/status behavior before assuming a cloud backup API issue.
- `..\jibo\sdk\skills\nimbus\tests` and `..\jibo\pegasus\packages\integration-tests-int\src\listen*.test.ts` show the cloud/Nimbus contract: listen transactions emit `SOS`, `EOS`, and `LISTEN`, with optional `SKILL_ACTION`; matched responses preserve `match.skillID` or `match.cloudSkill`; `CLIENT_ASR` and `CLIENT_NLU` should both be first-class test inputs.
- `..\jibo\pegasus\packages\report-skill\tests\subskills\News.test.js` is the best source-backed guide for news expansion: use category preferences, filter unusable or duplicate items, gate adult headlines for children or unidentified speakers, and provide image metadata alongside spoken headlines.

When sources disagree, prefer the newest live stock-OS capture for runtime behavior, then stock robot source for local ownership, then Pegasus for original cloud intent, then Node for known working compatibility behavior.

## `1.0.18` Closeout Gates

Before calling `1.0.18` complete, prove or explicitly defer these:

- Run the focused `.NET` cloud test suite after the last feature slice.
- Run the current-release live checklist in [regression-test-plan.md](regression-test-plan.md).
- Confirm the running robot build reports cloud version `1.0.18` using the shorter `Cloud version ...` wording, without stopping itself on a hotphrase, reopening a late `LISTEN`, or producing a follow-up `Cloudford` / generic chat tail.
- Confirm cloud-version and one generic Nimbus/chat turn include `match.skipSurprises = true` and do not transition into `@be/surprises` / `@be/surprises-ota` after speech completes.
- Regression test alarm flows again after the `jibo test 30` duplicate-clock-handoff fix: set with explicit time, set with compact/spoken/comma-separated time, clarify missing time, replace an existing alarm, cancel/delete by voice including `delete the alarm`, cancel out of a value prompt, and verify the menu agrees.
- Regression test timer flows after the Test 25 stale-timer observation: set a 10-second timer, let it fire, reset by gesture only after recording state, and verify a new timer prompt does not see an already-expired timer as still active.
- Regression test photo/gallery flows again after the `jibo test 26` fixes: open gallery, answer the stock `shared/yes_no` prompt with a transcript-bearing `yes`, hand into create, take one photo, keep it, and avoid blue-ring, `I heard you`, or `that's` stale turns after gallery cleanup.
- Live-test radio launch: `open the radio` passed in `jibo test 22`; re-run `play country music` if that exact phrase was not captured.
- Treat basic news as live-proven by `jibo test 23`; defer provider-backed or category-expanded news unless it is chosen as an optional feature slice.
- Regression test the added stop and volume slices after the Test 26 fixes: `stop that`, `never mind`, `turn it up`, `turn it down`, `set volume to six`, `set volume to 6`, and `show volume controls`.
- Recheck constrained yes/no prompts for update/backup/share/gallery/alarm replacement without leaking global rules.
- Recheck that stock OS no longer logs OpenJibo-only websocket events such as synthetic pending/context/ack packets from the current build.
- Recheck backup/update behavior with explicit attention to robot-local `jibo.scheduler.backupStatus`, the local `@be/idle` nighttime OTA helper, CPU/load, log/upload activity, and whether the deployed cloud is involved at all.
- Treat remaining empty-ASR, `ffmpeg`, or `whisper.cpp` transcript failures as STT work unless the capture proves a separate turn-routing regression.

## Known Gaps

These are not blockers for calling `1.0.18` complete unless the live test shows a regression in a current release path:

- local `whisper.cpp` STT remains a discovery seam, not production ASR
- media upload/body handling is not binary-safe enough for final gallery originals and thumbnails
- state persistence is local JSON, not Azure SQL / Blob Storage
- update, backup, and restore are not end-to-end proven, and the `jibo test 22` / Test 26 / Test 27 / Test 28 sluggishness appears tied to robot-local backup status/load, startup reconnect state, or previously unsuppressed end-of-skill surprises; Test 31 also captured a legacy `Backup_20170222.List` startup query, which reinforces that the local backup/status path is real even before a user asks for backup
- Tests 27 and 28 showed backup/surprise behavior without corresponding `Backup_*` HTTP traffic; Test 28 isolated the unsuppressed `@be/surprises` lifecycle handoff after Nimbus
- deployed-build verification needs to prove that synthetic OpenJibo websocket events are gone from the hosted artifact, not just from source
- news content is synthetic; `jibo test 23` proved the path but not live provider-backed headlines
- alarm replacement yes/no, alarm voice delete/menu agreement, empty-gallery voice `yes`, and long blue-ring cleanup still need successful live proof after the Test 30 source fixes
- weather, calendar, commute, personal report, identity, memory, and proactivity are still mostly discovery or placeholder content paths
- remaining stop/volume variants still need live stock-OS proof beyond Test 26's `Never mind.` and `Set Volume 2-6.` passes; robot age and command-versus-question personality routing are not implemented yet

## `1.0.19` Direction

After `1.0.18` is tested and tagged, `1.0.19` should move back into feature work:

- harden whichever stop/volume behavior is not fully proven by the `1.0.18` live pass, or pick the next lightweight device/persona slice
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
