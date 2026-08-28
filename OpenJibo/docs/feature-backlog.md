# Feature Backlog

## Purpose

This backlog turns discovery into implementation slices for the hosted `.NET` cloud.

Use it as the working queue when picking the next feature or bug-fix slice. The release pattern is: implement a narrow slice, test it on stock OS `1.9`, update this file with what happened, then either close the release or roll the next larger idea forward.

The live regression checklist for release closeout is [regression-test-plan.md](regression-test-plan.md).

The active `1.0.20` execution shape is tracked in [release-1.0.20-plan.md](release-1.0.20-plan.md). This file keeps the full `1.0.18` evidence trail for parity reference and the `1.0.19` closeout history alongside the new queue.

## Managed Hosting Integration Boundary

The neutral runtime must support multiple hosting choices without carrying a specific operator's price, billing provider, commercial entitlement policy, or customer-support process.

Shared `1.0.20` priorities remain here:

1. complete [Replace Snapshot-Backed In-Memory Cloud State](#13-replace-snapshot-backed-in-memory-cloud-state)
2. keep durable state normalized and payload bytes in Blob/file adapters while bounding connection/turn/audio memory
3. stabilize provider-neutral identity, provider registry, signed onboarding handoff/return, explicit selection, export, revocation, and recovery contracts
4. keep self-hosted isolated operation independent of a managed service
5. define conversion profiles with JiboAutoMod for managed, community-hosted, owner-managed, hybrid, isolated, and developer choices
6. keep OpenJibo.com neutral and clearly identify every hosted provider/operator

Transcendent Software's `$5/month/robot` managed membership, billing, entitlements, hosted backup policy, customer site, support, funds transparency, and paid pilot are implemented privately and represented publicly by [cloud.openjibo.com](https://cloud.openjibo.com).

Status key:

- `implemented`: present in current source and covered by focused tests
- `polish`: implemented enough to test, but still needs live proof or small cleanup
- `ready`: grounded enough to implement now
- `in progress`: implementation exists but the named exit evidence is incomplete
- `not started`: required scope is understood but implementation has not begun
- `decision required`: engineering is waiting on an explicit product, business, legal, or operational choice
- `discovery`: more Pegasus, JiboOS, capture, or log work needed first
- `blocked`: waiting on infrastructure, provider choice, or a risky unknown

Tags:

- `protocol`: websocket, HTTP, or stock payload shape
- `content`: provider data or response content
- `docs`: operator docs, runbooks, or capture process
- `stt`: transcript reliability
- `storage`: persistence, media, backups, or hosted export
- `reliability`: connection recovery, service lifecycle, or production resilience

## Historical `1.0.18` Snapshot

Historical cloud version at closeout boundary: `1.0.18`

Runtime truth:

- hosted `.NET` projects and cloud tests target `net10.0`
- version source of truth is [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- `/health`, startup logging, and spoken `cloud version` are aligned with that constant
- spoken `cloud version` is now a one-shot diagnostic with speech-tail protection instead of a follow-up chat turn

Current release theme:

- alarm and photo/gallery quirks have received the main bug-fix attention
- Word of the Day cleanup, constrained yes/no routing, unknown websocket event suppression, and local state persistence are already in the current code
- radio, ESML apostrophe cleanup, and first news are implemented in source/tests; radio and basic news are live-proven as of `jibo test 23`
- `jibo test 22` validated radio, exposed backup/load interference, exposed a shared yes/no no-input gap, exposed repeated create keeper prompts after photo handoff, and showed local whisper `ffmpeg` failures on unusable buffered audio
- `jibo test 23` validated basic news, proved one alarm set/fire path at `7:43 AM`, exposed comma-separated/short alarm follow-up parsing risk, showed stock alarm replacement yes/no rules that needed cloud handling, and showed photo gallery still failing when `shared/yes_no` ASR came back empty
- personal report parity now has loop-scoped calendar and commute provider seams that merge persisted loop events, birthday/holiday dates, and commute profiles; the remaining report gap is richer travel-time data, not missing structure
- `jibo test 24` showed alarm replacement yes/no working, but exposed empty `clock/alarm_set_value` and `gallery/gallery_preview` turns falling into generic `I heard you` fallback speech; it also showed `CLIENT_NLU cancel` inside `clock/alarm_set_value` re-asking for an alarm value instead of closing the prompt
- `jibo test 25` proved a broader regression path but exposed repeated backup-in-progress/update-menu blockage, timer/alarm stale state and delete/menu disagreement, gallery `shared/yes_no` hangs under `@be/gallery`, punctuated `Never mind.` falling through to chat, volume homophone parsing (`Set Volume 2-6.`), and settings volume-control cleanup falling into `I heard you`
- `jibo test 26` live-proved punctuated stop, volume homophone parsing, gallery launch/yes/create/save, and good morning; it still exposed robot-local backup warnings, long blue-ring buffering without a fresh `LISTEN`, alarm replacement drifting into the value/manual screen, and alarm delete phrases/mishears falling to chat
- `jibo test 27` isolated early confusion: local `jibo-server-service` restarted and raised `Q4-Server_connection_lost` before testing; cloud version then self-listened into `Cloudford.` because the previous diagnostic path stayed follow-up eligible; the backup warning again came from local `@be/surprises-ota` with no `Backup_*` HTTP calls
- `jibo test 28` isolated the follow-on backup doorway: cloud-version/generic Nimbus matches had `skipSurprises` unset, then stock BE requested `@be/surprises` after Nimbus settled; VAD inhibited the offer in Test 28, while Test 27 selected `@be/surprises-ota` through the same local lifecycle path
- `jibo test 29` confirmed `skipSurprises = true` was reaching stock BE and no backup announcement repeated in the focused run, but the cloud-version answer still interrupted because the spoken diagnostic included `Jibo` and triggered local hotphrase barge-in during Nimbus TTS
- `jibo test 30` confirmed cloud-version now speaks cleanly; it still exposed a local gallery-to-`@be/surprises-ota` backup announcement, missing visible empty-gallery voice listen, and a duplicate alarm clock relaunch after `638` was parsed locally as `6:38 PM`
- `jibo test 31` showed the remaining alarm/backup wrinkle in full: startup logged a legacy `Backup_20170222.List` request before the first voice turn, `7:11 AM` collapsed into `7:00 PM` / `setting alarm for seven`, and the later clock `No` replied `that's fine` before the robot opened `@be/surprises` and ended in a blue-ring listen loop until reset
- `jibo test 32` suggests the alarm set path is improving, but the remaining regression surface is now sharper: an alarm can still leave the listen open at the end, the proactive Word of the Day `Yes` branch can miss its yes/no slot and echo back, and delete-alarm retry still falls into a second `set one?` question with a broken follow-up reply

## Immediate `1.0.18` Queue

### 1. Radio Resume And Genre Launch

- Status: `polish`
- Tags: `protocol`
- Why now: the code path is implemented and test-backed, and it is a low-risk local-skill expansion after Word of the Day.
- Current code:
  - `open the radio` maps to `@be/radio` with `intent = menu`
  - `play country music` maps to `@be/radio` with `entities.station = Country`
  - websocket output includes `LISTEN`, `EOS`, local `SKILL_REDIRECT`, and silent completion
- Evidence:
  - JiboOS `@be/radio` treats `menu` as a play launch and reads `result.nlu.entities.station`
  - `Country` is a supported station key in the inspected genre metadata
  - `jibo test 22` radio live validation passed
- Exit criteria:
  - live `open the radio` resumes or opens radio without generic chat speech
  - live `play country music` opens a country station
  - no new stock-OS unknown-event noise appears in the radio launch path
- Next action:
  - run this in the `1.0.18` live regression pass and capture both websocket payloads and robot logs

### 2. News Through Nimbus

- Status: `implemented`
- Tags: `protocol`, `content`
- Why now: the first Nimbus-compatible cloud path is implemented, test-backed, and live-proven; content can stay synthetic for `1.0.18`.
- Current code:
  - `tell me the news` maps to `IntentName = news`
  - outbound listen match includes `cloudSkill = news`
  - `SKILL_ACTION` uses skill id `news` and `mim_id = runtime-news`
- Evidence:
  - JiboOS Nimbus checks `match.cloudSkill === "news"` and waits for a cloud response
  - `jibo test 22` captured the phrase `So, play the news.` reaching the `news` intent, but live behavior was not cleanly confirmed
  - `jibo test 23` successfully played the synthetic quick brief
  - original Pegasus `report-skill` news tests cover the next expansion shape: category preferences, default categories, duplicate filtering, missing-summary filtering, child/unidentified-speaker content filtering, and headline image metadata
- Exit criteria:
  - live `tell me the news` reaches the Nimbus-shaped path
  - the robot behavior feels like a cloud skill response, not generic chat playback
- Next action:
  - keep the basic path in regression; provider-backed or category-expanded headlines can wait for `1.0.19` unless chosen as the optional feature slice

### 3. Backup / OTA / Share Yes-No Reliability

- Status: `polish`
- Tags: `protocol`, `stt`
- Why now: constrained yes/no behavior affects daily-use prompts and was tangled with the alarm/photo/gallery work.
- Current code:
  - yes/no detection reads `listenRules`, `clientRules`, and `$YESNO` hints
  - covered prompt families include `settings/download_now_later`, `surprises-ota/want_to_download_now`, `surprises-date/offer_date_fact`, `shared/yes_no`, `create/is_it_a_keeper`, `clock/alarm_timer_change`, and `clock/alarm_timer_none_set`
  - outbound replies strip global rules and keep the local rule
  - no-input fallback for constrained prompts emits local `LISTEN`/`EOS`
  - `shared/yes_no` now participates in the STT-failure no-input path instead of staying pending behind `$YESNO` hints
  - repeated empty `create/is_it_a_keeper` replies redirect to `@be/idle` after the second miss
  - hosted turn results, fallback matches, and local skill redirects include `match.skipSurprises = true` so BE does not launch end-of-skill surprises after normal replies
- Latest evidence:
  - `jibo test 22` did not show `Backup_*` HTTP traffic during the backup complaint
  - `jibo test 25` again showed backup-in-progress/update-menu blockage without `Backup_*` HTTP traffic; observed cloud traffic was log upload, ASR binary upload, and update check traffic
  - `jibo test 26` again had the robot announce backup-in-progress from `@be/surprises-ota`, with no `Backup_*` HTTP target in the capture
  - `jibo test 27` repeated that pattern in a smaller capture: the only relevant hosted startup traffic was token/update/log style traffic, while the spoken backup warning was selected by local `@be/surprises-ota`
  - Test 27 also showed local `jibo-server-service` reconnect and `Q4-Server_connection_lost` before the voice test, so startup health should be checked before blaming backup prompts on hosted cloud behavior
  - `jibo test 28` showed no hosted backup trigger in the focused cloud-version window, but did show BE opening `@be/surprises` after a Nimbus turn because the outgoing match did not carry `skipSurprises`
  - stock `@be/surprises-ota` drives the backup notification from robot-local `jibo.scheduler.backupStatus`
  - original `surprises-ota` tests make backup and OTA notifications contextual-priority prompts, with repeat suppression through last-notification timestamps
  - a spoken `take a backup` command currently routes as generic chat and is not the same as proving the local backup scheduler path
  - `jibo test 23`, `jibo test 25`, and `jibo test 26` showed backup-in-progress sluggishness or warnings while backups were active; explicit backup voice launch remains unwired
  - Test 26 suggests this should be investigated beside robot-local scheduler status and log/upload load rather than only hosted backup APIs
  - `jibo test 30` showed the backup announcement after gallery came from local `@be/surprises` -> `@be/surprises-ota`, not from a hosted `Backup_*` HTTP call; the local `@be/idle` nighttime OTA helper can also initiate backup through `jibo.scheduler.backupRobot`
  - `jibo test 31` added a startup `Backup_20170222.List` capture before the voice session, which is useful evidence that the legacy backup-status path is active even when the user did not ask for backup
- Parity plan:
  - keep the current cloud `GetUpdateFrom` no-content compatibility only as a short-term updater bridge
  - add robot-local scheduler/status behavior that matches the original contracts instead of asking the cloud manifest to model menu state
  - model the original paths separately: `backupStatus` as boolean, `downloadStatus` as null-or-progress object, and `checkForUpdates` as an `updates[]` response
  - let the update menu decide among `backup`, `downloading`, `updates`, and `none` using those local status calls
  - keep the robot updater path compatible with `jibo-get-update` / `jibo-download-update` until the robot-side caller is fixed
- Exit criteria:
  - spoken `yes` and `no` work on update, backup, share/offer, and gallery/create prompts
  - empty or missed short replies retry locally instead of relaunching Nimbus or generic chat
  - ordinary Nimbus/chat/cloud-version turns settle without `@be/surprises` / `@be/surprises-ota` opening afterward
- Next action:
  - re-run these prompt families in the `1.0.18` live regression pass after the shared yes/no, alarm yes/no, and create no-input fixes
  - verify websocket `match.skipSurprises = true` on cloud-version, generic chat, fallback/no-input, and at least one local redirect
  - keep explicit backup creation as part of the update/backup/restore proof slice, not as an assumed yes/no prompt test

### 4. Alarm And Photo Gallery Release Regression

- Status: `polish`
- Tags: `protocol`, `stt`
- Why now: this is the main bug-fix theme for `1.0.18`.
- Current code:
  - alarm values parse explicit, compact, spaced, comma-separated, hyphenated, and local-context ambiguous times
  - short alarm/timer value replies are accepted during clock value follow-up rules instead of being filtered out before parsing
  - local clock value follow-up rules now return only `LISTEN`/`EOS`, avoiding the Test 30 duplicate delayed `@be/clock` relaunch after stock clock already consumed a short time reply
  - empty alarm/timer value turns complete locally as no-input instead of falling through to generic Nimbus speech
  - missing alarm times stay in local `@be/clock` clarification
  - alarm cancel can reuse the last active clock domain
  - cancel inside a clock value prompt maps to local clock `cancel`
  - stock alarm replacement/no-alarm prompts use the constrained yes/no path
  - gallery opens as `@be/gallery`; snapshot and photobooth open through `@be/create`
  - empty `gallery/gallery_preview` turns complete locally as no-input instead of relaunching Nimbus fallback speech
  - passive gallery/create/settings context no longer reopens stale cloud turns
  - active local prompts under gallery/settings contexts are preserved so real short replies are not suppressed as passive context
  - context-only or post-skill binary audio tails are ignored until a fresh `LISTEN`, preventing no-`LISTEN` blue-ring buffering loops
  - fresh no-transcript hotphrase launch `LISTEN` setup packets are ignored during diagnostic speech-tail cleanup, preventing the Test 27 `Cloudford.` self-listen path
  - blank-audio hotphrase turns clear pending listen state and install a short late-audio ignore window
  - `shared/yes_no` no-input fallback and repeated create keeper cleanup were added after `jibo test 22`
- Latest evidence:
  - gallery opened and handed into create, but repeated `create/is_it_a_keeper` prompts could leave the blue ring/listening state
  - alarm recognition collapsed several attempts before a complete alarm value could be set
  - `ffmpeg` failures were present during the same test window, so alarm/gallery retest should separate transcript quality from payload shape
  - `jibo test 23` set and fired a `7:43 AM` alarm, then failed a later clarify/replacement path when the robot heard `- Time. - 7, 14.` and stock NLU converted that to `7:00 PM`
  - `jibo test 23` photo gallery got stuck on `shared/yes_no` turns with empty ASR, not on a transcript-bearing `yes` that the cloud mapped incorrectly
  - `jibo test 24` recognized `Yes.` for `clock/alarm_timer_change`, but empty `clock/alarm_set_value` produced `I heard you`; current source now keeps that as local no-input
  - `jibo test 24` showed photo/gallery blue-ring cleanup improved and create keeper completion working, but empty `gallery/gallery_preview` produced `I heard you`; current source now keeps that as local no-input
  - `jibo test 25` showed gallery launching from the observed phrase `open the photogal`, but active `shared/yes_no` prompts under `@be/gallery` could hang; current source now recognizes the alias and preserves active gallery prompts even while ignoring passive gallery tails
  - `jibo test 25` showed timer/alarm still needs live follow-up for stale timer state, alarm replacement/PM ambiguity, and voice delete versus robot menu agreement
  - `jibo test 26` showed gallery success through empty-gallery yes, create, keep, save, and reopen, but also showed a post-gallery blue-ring/fallback tail now addressed by the no-`LISTEN` binary guard
  - `jibo test 26` showed alarm replacement still drifting into value/manual-screen behavior and alarm delete phrases/mishears falling to chat; current source now maps `delete the alarm`, `delete along`, and `delete the along` to local clock delete without keeping follow-up open
  - `jibo test 27` showed the no-`LISTEN` guard worked for same-transID binary tails, but a new hotphrase launch `LISTEN` could still capture diagnostic speech tail; current source now blocks that diagnostic-tail shape
  - `jibo test 30` showed cloud-version fixed, but the empty-gallery prompt did not visibly light the blue ring for a voice `yes`; treat the next gallery pass as a proof of local `shared/yes_no` listen ownership, not just cloud payload shape
  - `jibo test 30` showed `638` was processed at 6:38:13 AM and stock clock resolved it to `6:38 PM`; the duplicate replacement prompt matched our extra delayed clock relaunch, now suppressed for local clock follow-up rules
  - `jibo test 31` showed `7:11 AM` collapsing to `7:00 PM` / `setting alarm for seven`, then a clock `No` producing `that's fine` before the robot opened `@be/surprises`; the later retry sat in a continuous blue-ring/listen loop until reset
  - original clock tests confirm cancel inside the alarm value prompt must close without scheduling, existing-alarm `keep` must preserve KB/scheduler state, and existing-alarm `delete` or `cancel` must clear it
  - original gallery tests confirm empty-gallery `yes` redirects to `@be/create`, empty-gallery `no` exits, media-load failure exits, and delete confirmation only deletes on a positive `yes`
- Exit criteria:
  - gallery opens, offers to take a picture if empty, accepts `yes`, and hands into create
  - alarm set, clarify, replacement yes/no, cancel from value prompt, and cancel/delete flows behave locally and agree with the menu state
  - alarm replacement and deletion regression checks verify both websocket payload shape and persistent robot menu state where possible
  - short alarm/timer follow-up values do not produce a second `@be/clock` relaunch after the local skill consumes the answer
  - failures caused by collapsed STT transcripts are logged as STT issues rather than misdiagnosed as payload bugs
- Next action:
  - re-run a stock OS `1.9` regression bundle before declaring `1.0.18` complete

### 5. Optional Small Feature Before `1.0.18` Freeze

- Status: `implemented`
- Tags: `protocol`
- Why now: the user wants one or two features before `1.0.18` is called complete, but the release should not take on a risky subsystem.
- Selected slices:
  - Stop command
  - Volume up / volume down / set-to-value voice control
- Current code:
  - `stop`, `stop that`, and `never mind` map to stock `global_commands` `stop` NLU plus local `@be/idle` redirect/completion
  - `turn it up` and `turn it down` emit stock `global_commands` `volumeUp` / `volumeDown` with `volumeLevel = null` and no cloud speech
  - `set volume to six` emits stock `global_commands` `volumeToValue` with `volumeLevel = 6` and no cloud speech
  - `show volume controls` redirects into `@be/settings` with `volumeQuery`
  - stop/cancel matching now normalizes stock ASR punctuation, so `Never mind.` is still a stop command
  - absolute volume parsing now treats the observed homophone shape `Set Volume 2-6.` as level `6`
  - passive settings context and `settings/volume_control` no-input cleanup now avoid post-panel `I heard you` fallback speech
  - local clock delete/cancel commands now settle without a generic follow-up mic
- Evidence:
  - Pegasus `globals/global_commands_launch.rule` defines `stop`, `volumeUp`, `volumeDown`, and `volumeToValue`
  - stock Jibo `VolumePlugin` subscribes to global volume events and uses the same intent/entity names
  - stock `@be/settings` exposes `volumeQuery` and opens the volume panel
  - `jibo test 26` live-proved punctuated `Never mind.` and the `Set Volume 2-6.` homophone path
- Exit criteria:
  - live stop settles the robot without a generic chat reply
  - live volume up/down audibly changes volume or logs a local volume event
  - live volume-to-value changes the setting to the requested value or logs the expected stock local handling
  - live volume controls opens the settings volume panel
  - live volume controls settles after the panel opens without a trailing `I heard you`

## Implemented In Current Source

### ESML Apostrophe Encoding Bug

- Status: `implemented`
- Tags: `polish`
- Result:
  - apostrophes remain natural in spoken ESML
  - `&`, `<`, `>`, and `"` are still escaped
  - covered by `ResponsePlanMapper_EscapesSpeechWithoutEncodingApostrophes`
- Follow-up:
  - none unless a live capture proves another ESML escaping edge case

### Radio First Pass

- Status: `implemented`
- Tags: `protocol`
- Result:
  - phrase routing and websocket redirect/completion are implemented for radio resume/open and genre launch
- Follow-up:
  - live validation remains in the immediate queue

### News First Pass

- Status: `implemented`
- Tags: `protocol`, `content`
- Result:
  - Nimbus-shaped `news` cloud-skill lane is implemented with synthetic briefing content
- Follow-up:
  - basic live validation passed in `jibo test 23`
  - provider-backed headlines belong in `1.0.19` or later

### Clock / Alarm Family

- Status: `implemented`
- Tags: `protocol`
- Result:
  - time/date/day and clock open route through local `@be/clock`
  - timer/alarm menu, value, clarify, and delete are implemented
  - compact, spoken, comma-separated, and local-context alarm parsing has focused tests
  - short clock value replies under `clock/alarm_set_value` and `clock/timer_set_value` are not filtered out by websocket finalization
  - empty clock value turns produce local no-input instead of generic Nimbus fallback speech
  - `CLIENT_NLU cancel` inside a clock value prompt maps to local clock `cancel`
  - alarm replacement/no-alarm yes/no prompts are mapped as constrained local prompts
  - client NLU alarm clarify/cancel cases from `jibo test 20`, `jibo test 21`, and `jibo test 24` are reflected in source
- Follow-up:
  - live regression remains in the immediate queue
  - add fixture coverage for original clock-test branches that are not yet mirrored in `.NET`: no-alarm query `yes`/`no`, existing-alarm `keep` versus `delete`, and cross-domain `OtherSet` behavior
  - Test 26 still requires a focused live check for alarm replacement, voice delete versus menu state, and whether the no-`LISTEN` guard removes the long blue-ring loop

### Photo / Gallery / Create Family

- Status: `implemented`
- Tags: `protocol`, `storage`
- Result:
  - gallery, snapshot, and photobooth voice paths route to the correct local skills
  - the observed `open photogal` transcript routes to gallery
  - media metadata persists locally
  - `/media/{path}` serves the current text-body placeholder payload
  - empty `gallery/gallery_preview` turns produce local no-input instead of generic Nimbus fallback speech
  - active `shared/yes_no` prompts under `@be/gallery` stay active instead of being suppressed as passive local context
  - repeated empty `create/is_it_a_keeper` turns redirect to `@be/idle` after the second miss
- Follow-up:
  - live regression remains in the immediate queue
  - add fixture coverage for original gallery-test branches that are not yet mirrored in `.NET`: empty-gallery `yes` redirect to create, empty-gallery `no` exit, media-load failure exit, and delete confirmation `yes`/`no`
  - final gallery original/thumbnails surfacing remains a later follow-up on top of the binary-safe storage seam

### Constrained Yes-No Cleanup

- Status: `implemented`
- Tags: `protocol`, `stt`
- Result:
  - `shared/yes_no` is included in yes/no STT-failure detection
  - local no-input replies strip global rules and keep the active constrained rule
  - update, OTA, share/date-offer, gallery shared yes/no, alarm replacement/no-alarm, and create keeper rules share the same no-input fallback machinery
- Follow-up:
  - live update/backup/share/gallery/alarm replacement prompts still need another clean pass

### Cloud Version Tail Cleanup

- Status: `implemented`
- Tags: `protocol`
- Result:
  - `cloud_version` no longer keeps the generic follow-up mic open
  - diagnostic speech receives an eight-second late-audio ignore window
  - no-transcript hotphrase launch `LISTEN` setup packets inside that cleanup window are ignored before they can reopen a stale turn
  - spoken diagnostic wording is now `Cloud version ...` rather than `Open Jibo Cloud version ...`, avoiding the self-hotphrase phrase found in Test 29
  - focused websocket coverage reproduces the Test 27 `Cloudford.` shape: cloud-version speech, tail `LISTEN`, and binary speech tail
- Follow-up:
  - live smoke should confirm `cloud version` speaks `1.0.18`, carries `match.skipSurprises = true`, does not stop itself on the word `Jibo`, and settles without a generic `I heard...` reply or a local surprise handoff

### GLSM Listener Flow Capture And Recovery

- Status: `implemented`
- Tags: `protocol`, `docs`
- Result:
  - the legacy listener state machine source (`sdk ... glsm.png`) is now captured in current planning docs
  - runtime now emits GLSM-aligned phase snapshots (`HJ_LISTENING`, `LISTENING`, `WAIT_LISTEN_FINISHED`, `DISPATCH_DIALOG`, `PROCESS_LISTENER_QUEUE`)
  - turn diagnostics now include `glsm_phase_transition` for phase changes
  - websocket telemetry now records `glsmPhase` on binary/context/turn events
  - stale pending-listen recovery is now in source so a long-open no-context/no-audio listen can be cleared when the next hotphrase listen arrives
- Follow-up:
  - live-capture proof is still required against the recurring blue-ring/stuck-listening sequence
  - deeper GLSM parity (`Interrupt Listeners`, launch/global parse branches) should be tackled after this first capture slice is validated on-device

### End-Of-Skill Surprise Suppression

- Status: `implemented`
- Tags: `protocol`
- Result:
  - hosted `LISTEN` matches, fallback `LISTEN` matches, and local `SKILL_REDIRECT` matches emit `skipSurprises = true`
  - focused websocket assertions cover generic chat, cloud version, no-transcript fallback, and a local clock redirect
  - Test 28 evidence ties the repeated backup warning to the local `@be/surprises` lifecycle path after Nimbus, with no corresponding hosted `Backup_*` traffic
  - Test 29 showed the deployed payload reached stock BE and did not repeat the backup announcement in the focused run
- Follow-up:
  - live regression should confirm normal Nimbus/cloud/local turns no longer open `@be/surprises` or `@be/surprises-ota` after completion

### Word Of The Day Cleanup

- Status: `implemented`
- Tags: `protocol`
- Result:
  - voice launch uses menu-shaped local payload plus redirect/completion
  - structured and spoken guesses complete correctly
  - line-number guesses use hint order
  - close hint matching handles near misses
  - `right_word` cleanup can no-input close and redirect to `@be/idle`
  - late same-turn audio is ignored during cleanup
- Follow-up:
  - keep this in regression coverage because it shares turn-state machinery with gallery and alarm flows

### Stop And Volume First Pass

- Status: `implemented`
- Tags: `protocol`
- Result:
  - global stop commands emit stock `global_commands` `stop` and redirect to `@be/idle`
  - stop/cancel command matching tolerates punctuation from stock ASR
  - relative volume commands emit stock `global_commands` `volumeUp` / `volumeDown`
  - absolute volume commands emit `volumeToValue` with a `volumeLevel` entity, including the observed `Set Volume 2-6.` shape
  - volume controls launch redirects to `@be/settings` `volumeQuery`
  - passive settings context and `settings/volume_control` no-input cleanup avoid stale generic speech after the settings panel opens
  - websocket responses avoid generic chat speech for these local/global command paths
- Follow-up:
  - live validation remains in the immediate queue because volume depends on stock robot local global-command handling

### Unknown OpenJibo Event Noise

- Status: `implemented`
- Tags: `protocol`
- Result:
  - current websocket service drops unknown inbound message types silently
  - synthetic `OPENJIBO_TURN_PENDING`, `OPENJIBO_CONTEXT_ACK`, and fallback `OPENJIBO_ACK` should no longer be emitted by current source
- Follow-up:
  - `jibo test 22` still captured those event types from the deployed run, so the next deployment must verify the artifact/build as well as source

### Update Phantom Manifest Fix

- Status: `implemented`
- Tags: `protocol`, `storage`
- Result:
  - `GetUpdateFrom` returns no content when no update is staged
  - staged updates can still be created explicitly
- Follow-up:
  - end-to-end update delivery and restore proof remains future work

## `1.0.20` Launch Priorities

These are the carryover items that need a clean proof pass first:

1. Update / backup / restore parity
   - finish the update-menu investigation
   - prove whether the robot is fabricating an update path when none exists
   - keep backup and update state aligned with the robot-local behavior
   - verify the `ListUpdates`, `ListUpdatesFrom`, `GetUpdateFrom`, `CreateUpdate`, and `RemoveUpdate` shapes against the robot capture
   - separate menu-state truth from the compatibility bridge so `GetUpdateFrom` is not the source of truth
   - the current false-positive points at robot-side OTA KB state, especially `updatesAvailable`, not the cloud `GetUpdateFrom` placeholder
   - confirm update prompt state, backup prompt state, and restore rehydration are each driven by the right local status source
   - capture the minimum live or replayable path that shows update, backup, and restore without a phantom update announcement
   - hold the cloud compatibility bridge only for the updater helper until robot-local state is fully proven
   - exit criteria: the robot does not invent an update path when none exists, backup state is reported correctly, and restore is understood as persisted-state rehydration
   - status: `implemented`
   - current progress: focused protocol coverage now passes for `GetUpdateFrom`, `ListUpdatesFrom`, scheduler update wrapping, and the backup / restore round-trip, so the lane has a test-backed closure point instead of just a planning note
2. Grocery list follow-up and add-item reliability
  - grocery follow-up listen is now emitted from the cloud path; finish hardware verification and any robot-side parity gaps rather than inventing a new capture flow
   - keep the list interaction listening for the follow-up item instead of dropping back to a passive state
   - verify long add-item phrases still reach the list engine cleanly
   - cloud parser coverage now includes polite long-form inline adds such as `can you add ... for my grocery list`,
     `could you please add ... in my shopping list`, and `would you add ... for my to do list`
   - status: `implemented`
   - current progress: the cloud follow-up flow, inline add parsing, and focused websocket tests already cover the grocery/shopping/to-do capture path; any remaining check is live robot playback rather than backlog discovery
3. Motion and personality command parity
- keep `go to sleep` from drifting into the wrong visible state; the legacy path is a real sleep global, the ASLEEP state is event-driven rather than timer-driven, wake is driven by `dayStarts`, `headTouch`, or `hjHeard`, and the legacy sleep behavior tree includes a sleeping-idle loop that we need to preserve so the robot stays visibly asleep
   - keep `turn around` and other motion verbs source-backed; the legacy snapshot backs the lane through `spin around` / `twirl`
   - separate bare `twerk` from the greeting-looking fallback while preserving `can you twerk`; the intent is source-backed and the remaining gap is robot-side STT mishearing
   - status: `polish`
   - current progress: the cloud command routing and websocket coverage already prove `sleep`, `wake_up`, `turn around`, `spin around`, `twirl`, and `twerk` paths, but the live robot still shows the sleep-double-launch quirk and occasional motion no-op behavior that should stay on the review list
4. Circadian sleep / wake state-machine parity
   - status: `ready`
   - tags: `protocol`, `docs`
   - architecture reference: [system-diagram-alignment.md](system-diagram-alignment.md)
   - scope:
     - preserve the original `@be/idle` circadian flow as `ALERT` / `RELAXED` / `NAP` / `FALLING_ASLEEP` / `ASLEEP` / `WAKING_UP` / `TURN_AWAY`
     - keep the robot-side sleep state distinct from the cloud-side session mirror
     - wire explicit wake events instead of flattening everything into a generic idle redirect
   - current code truth:
     - `sleep` already routes through `@be/idle`
     - the cloud session now remembers `sleepState=sleeping`
     - websocket diagnostics can report `ASLEEP`
   - missing piece:
     - explicit wake-event handling for `dayStarts`, `headTouch`, `hjHeard`, and related attention events
   - review note: the cloud side does not currently model those robot circadian wake triggers by name, so the remaining Pegasus-source parity check should be done on the other machine before we decide whether to mirror them here or leave them as robot-local behavior
   - exit criteria:
     - sleep enters a persistent asleep mode
     - wake events clear that mode cleanly
     - robot/cloud state stays aligned during the full sleep cycle
5. STT and turn-finalization cleanup
   - treat the bare `twerk` miss as an STT/parsing proof item until a robot capture proves the cloud path is at fault
   - `turn around` has been verified as working on the robot, so it no longer belongs in the STT cleanup bucket
   - keep short constrained replies and local prompts stable while the new regression items are retested
   - status: `polish`
   - current progress: the cloud turn-finalization path already covers the low-signal screen, filler-only rejection, wake/sleep boundaries, and the `twerk` / `can you twerk` boundary; the remaining proof target is the live robot mishearing path rather than a missing server rule
6. Broader personality and presence continuation
   - continue the source-backed favorites, presence, and seasonal ladder once the regression gaps are understood
   - status: `polish`
   - current progress: the favorites family, presence-aware greetings, and seasonal holiday batches are already source-backed in the cloud path, and the remaining work is mostly live playback / launch-state cleanup for the few prompts that still need robot proof

## Near-Term `1.0.20` Queue

### 6. Stop Command

- Status: `implemented`
- Tags: `protocol`
- User goals:
  - `stop`
  - `stop that`
  - `never mind`
- Evidence:
  - `@be/idle` exists and is already used as a cleanup redirect target
  - current `1.0.18` source emits stock `global_commands` `stop` plus local `@be/idle` redirect
- Questions:
  - whether live stock OS treats the combined global stop plus idle redirect as cleanly as expected during active local skills
- Exit criteria:
  - a spoken stop command settles the robot locally without a generic chat reply
- Progress update (`2026-07-28`):
  - focused interaction-service and websocket tests now cover `stop`, `stop it`, `forget it`, and the `@be/idle` / `global_commands` routing, so the stop lane can move from `polish` to `implemented`

### 7. Volume Up / Volume Down Voice Control

- Status: `implemented`
- Tags: `protocol`
- User goals:
  - `turn it up`
  - `turn it down`
  - `increase the volume`
  - `decrease the volume`
- Evidence:
  - Pegasus global commands define `volumeUp`, `volumeDown`, and `volumeToValue`
  - stock Jibo `VolumePlugin` listens for those global intents and `volumeLevel`
  - current `1.0.18` source emits those stock NLU shapes and opens `@be/settings` `volumeQuery`
- Questions:
  - whether live stock OS applies the global volume event from the hosted cloud response without any additional local event payload
- Exit criteria:
  - relative voice volume commands adjust volume without generic cloud speech

- Progress update (`2026-07-28`):
  - focused interaction-service and websocket tests now cover `turn it up`, `turn it down`, `increase the volume`, and `decrease the volume`, so the volume lane can move from `polish` to `implemented`

### 8. Update, Backup, And Restore End-To-End Proof

- Status: `implemented`
- Tags: `protocol`, `storage`, `docs`
- Closure note:
  - focused protocol tests now cover the no-staged-update path, same-version rejection, lower-bound update lookup, scheduler update wrapping, and the robot update / backup / restore round-trip
- Current evidence:
  - `@be/settings` contains update and backup flows
  - `@be/restore` waits for a UGC key, runs restore, and reboots
  - original OTA surprise tests treat backup/download status as robot-local scheduler state, not as a direct cloud backup command path
  - no-op update fabrication has been removed from `.NET`
  - Test 25 still showed repeated backup-in-progress/update-menu blockage without `Backup_*` HTTP traffic
  - Test 26 repeated the backup-in-progress warning from robot-local `@be/surprises-ota` without `Backup_*` HTTP traffic
  - Test 27 repeated the same no-`Backup_*` finding and added evidence of local startup reconnect / `Q4-Server_connection_lost` before the test
  - Test 28 showed the same class of surprise handoff beginning at `@be/surprises` after Nimbus, before VAD inhibited the offer
- Progress update (`2026-06-25`):
  - added a local scheduler `/apply-update` proof endpoint so a staged update can advance the stored robot firmware version, require reboot, and disappear from subsequent scheduler update checks instead of remaining as a phantom pending update
  - added focused protocol coverage for staging a controlled robot update, applying it, and verifying the robot profile platform plus empty follow-up `/check-updates` response
- Progress update (`2026-06-29`):
  - restore now accepts a mapped backup `location.url` object or location URL string in addition to `backupId`, `id`, and `etag`, matching the shape returned by `Backup_20170222.List` / `Create` so stock restore callers can round-trip the response without a manual ID transform
  - added focused protocol coverage proving both location restore shapes rehydrate the saved update snapshot and remove post-backup stray updates
- Progress update (`2026-07-28`):
  - reran focused protocol coverage for update lookup, scheduler wrapping, and backup / restore handling; the tests passed, so the release backlog entry can move from `ready` to `implemented`
- Exit criteria:
  - no phantom "always has updates" behavior
  - one controlled update can be staged and delivered
  - one controlled backup can be taken
  - restore behavior is documented well enough to recover a test robot intentionally

### 9. STT Upgrade And Noise Screening

- Status: `implemented`
- Tags: `stt`
- Closure note:
  - provider selection and low-signal guards now have focused test coverage, and the current code path supports both Azure Speech and local Whisper workflows in the ways we care about
- Current evidence:
  - `jibo test 22` showed `ffmpeg` and `whisper.cpp` failures
  - `jibo test 23` did not show the same decode failure pattern, but gallery yes/no turns still produced empty ASR
  - `jibo test 24` still had collapsed or empty transcripts in alarm/gallery paths, including `Sudden alarm.`, `I'm setting alarm for seven.`, empty clock value input, and empty gallery preview input
  - `jibo test 25` still had short-answer failures, but several were cloud turn-state issues now patched rather than pure STT failures
  - `jibo test 26` had long no-`LISTEN` binary buffering and alarm-delete mishears now patched; remaining short-answer failures still need STT/noise work
  - current source now skips local whisper when buffered audio does not contain an Opus identification header
  - yes/no and alarm flows are especially sensitive to short or collapsed transcripts
- Progress update (`2026-05-21`):
  - added a small local whisper noise floor so obviously tiny buffered audio can be screened before ffmpeg/whisper work runs
  - short/noisy buffered turns now fail fast instead of wasting a transcription cycle
  - focused tests now cover the new low-audio rejection behavior
- Progress update (`2026-07-28`):
  - reran focused Azure Speech, local Whisper, and low-signal WebSocket tests; they passed, so the STT lane can move from `in progress` to `implemented`
- Implementation notes:
  - add lightweight waveform or energy screening before transcription
  - compare managed STT against the local toolchain
  - keep synthetic transcript hints for fixture replay

### 10. Hosted Capture And Storage Plan

- Status: `implemented`
- Tags: `docs`, `storage`
- Why next:
  - repo-local captures work for single-operator testing, but group testing needs a cleaner archival/export boundary
- Implementation notes:
  - define local capture sinks versus hosted retention
  - decide how testers submit noteworthy sessions
  - keep a lightweight `capture-index.ndjson` manifest beside raw captures so testers can quickly find sessions, operations, and fixture exports
  - preserve sanitized fixtures as the durable parity artifact
- Architecture note:
  - [telemetry-production-safety.md](architecture/telemetry-production-safety.md) now records the production-safe split between Serilog logs and hosted capture storage, plus the rule that capture failures must never break request handling
- Progress update (`2026-06-24`):
  - websocket turn diagnostics now append to `capture-index.ndjson`, not only the raw daily event log, so short-answer probes, turn-boundary decisions, and STT guardrail events are findable in the same manifest as connections, messages, HTTP protocol captures, and exported fixtures
  - this keeps the local capture shape closer to the eventual hosted-retention boundary while the group-submission workflow is still being designed
- Progress update (`2026-07-28`):
  - focused protocol, websocket, and turn telemetry sink tests now pass, so the capture-index and fixture-export path is proven enough to move this lane from `in progress` to `implemented`

### 11. Binary-Safe Media Storage

- Status: `implemented`
- Tags: `storage`, `protocol`
- Why next:
  - the first gallery bridge stores metadata and text-body placeholders, but final gallery support needs originals and thumbnails
- Questions:
  - whether stock gallery expects originals, thumbnails, or both
  - what upload metadata must survive for gallery refresh
  - how to map this cleanly to Blob Storage
- Implementation notes:
  - media content now flows through a storage seam with file and Azure Blob adapters
  - the protocol still serves the legacy text-body contract, but the original payload is now persisted separately and can be swapped to binary-native storage later
- Progress update (`2026-07-28`):
  - focused media-create and cloud-state persistence tests now pass, so the binary-safe storage seam is proven enough to move this lane from `in progress` to `implemented`

### 12. Durable Robot Attribution And Hosted Artifact Audit

- Status: `polish`
- Tags: `protocol`, `storage`, `reliability`, `docs`
- Scope:
  - preserve explicit robot links and AWS credential claims without inferring ownership from traffic
  - keep archived duplicate records from reclaiming live sessions
  - capture HTTP, WebSocket ASR, Ogg/Opus, logs, and media artifacts with attribution provenance
  - expose session-link history and artifact capture coverage in the admin status page
- Current implementation:
  - robot header, bearer/session, SigV4/AWS3, and one-way credential fingerprint evidence are resolved centrally
  - credential claims, swaps, backfill, offline merge, verified serial evidence, and explicit session links are persisted
  - raw binary WebSocket audio is normalized into a single browser-playable Ogg/Opus stream
  - the status page distinguishes observed runtime IDs from explicit links and reports artifact coverage
- Remaining proof:
  - deploy and verify attribution survives restart/reconnect on the Azure state backend
  - perform a real capture audit covering events, logs, media, and ASR, including an unassigned-credential claim
  - confirm archived records remain historical after a fresh deployment

### 13. Replace Snapshot-Backed In-Memory Cloud State

- Status: `in progress`
- Tags: `storage`, `reliability`
- Priority: production reliability; complete before treating multi-replica hosting as safe
- Why now:
  - the `2026-08-16/17` production incident proved that a healthy `/health` response does not mean robot traffic can complete when persistence allocations are failing
  - production selects `PostgreSql`, but dependency injection still creates a singleton `InMemoryCloudStateStore`; `PostgreSqlSnapshotStore` only loads and rewrites one whole-state JSON document
  - the production `cloud-state` row reached `46,446,295` characters, and routine robot context traffic failed in `Utf8JsonWriter.WriteStringEscapeValue` while rewriting it inside a `2 GiB` Container App
  - five recursively embedded backup payloads had grown to approximately `0.8 MB`, `2.0 MB`, `4.3 MB`, `9.5 MB`, and `20.8 MB`
- Immediate mitigation already shipped:
  - backup snapshots no longer embed the backup catalog
  - legacy recursive backup payloads compact on load while preserving their restore content
  - the compacted production state was estimated at approximately `4.35 million` characters, about a `90%` reduction
  - focused backup/persistence tests and the full `2,000`-test cloud suite passed
- First normalized slice implemented:
  - PostgreSQL personal memory is stored in related tenant-scope tables and loaded per scope through a bounded TTL cache
  - legacy personal-memory snapshot import is idempotent and transactionally locked; the source snapshot remains available for rollback
  - the personal-memory connection pool defaults to 4 connections per replica and is configurable independently; cloud state defaults to 8, leaving connection headroom for two replicas under the current database limit
  - target-qualified migration scripts prevent personal-memory schema from being applied to the state database
  - issued authentication tokens and active dialog sessions now use separate registries; live sessions are capped, removed on disconnect, and excluded from snapshots without revoking durable tokens
  - buffered WebSocket audio is capped at 4 MiB per session and 64 MiB process-wide, rejects oversized frames, and is released on normal or abnormal socket teardown
  - explicit robot-to-inventory links now persist outside ephemeral session metadata so reconnect identity survives the session split
  - the state-target migration defines normalized relational tables and indexes for every durable cloud-state family; live WebSocket/turn objects are intentionally absent
  - the explicit legacy cloud-state importer is transactionally locked and idempotent, hashes issued tokens instead of copying plaintext, excludes live sessions, preserves the source snapshot, and externalizes legacy backup payloads with read-back hash verification
  - privacy-safe aggregate WebSocket connection/message/payload-byte metrics cover normal replies, push notifications, and Home Assistant traffic
  - generic WebSocket compression remains disabled: checked-in captures show already-compressed Ogg Opus audio is about 90% of inbound application bytes, and stock OS 1.9 `permessage-deflate` compatibility is not yet proven
- Completed implementation:
  - PostgreSQL production DI now selects normalized scoped repositories for every durable cloud-state family; file and SQLite self-hosting retain the compatibility store
  - whole-snapshot runtime writes are absent from the PostgreSQL path; active sessions are process-local and durable tokens are hashed relational records
  - scoped people/device hot paths and bounded 30-second caches avoid fleet-wide startup and interaction hydration while bounding cross-replica staleness
  - backup payloads live in Blob/file storage with hashed manifests; v2 loop restores are point-in-time, account-scoped, and atomic, while imported v1 backups remain restorable
  - the explicit importer is locked, transactional, idempotent, source-preserving, and fails startup safely when an unimported legacy snapshot is detected
  - aggregate traffic, active-session, accepted/rejected/current/high-water audio, active-turn, bounded turn phase/outcome, reply-batch, cache hit/miss, and configured pool metrics contain no robot/session identifiers or payload data
  - the browser/CLI release smoke supports configurable connected-robot tiers, simultaneous-turn percentages, rotating rounds, quiet holds, and client-observed turn latency summaries; CI tests the driver and staging exposes the bounded load controls
  - an initial staging proof held six fake notification sockets while two simultaneous turns completed with the expected reply order at 218 ms and 247 ms client-observed latency; this is driver evidence, not capacity certification
  - fleet peer sync now fails closed unless explicitly enabled with an exact host allowlist; the managed workflow forbids it in staging and applies the allowlist to both outbound and inbound reports, preventing cloned production trust rows from causing cross-environment calls
  - immediate staging containment removed the peer-sync shared-key environment reference and produced healthy revision `openjibo-cloud--0000009`; the post-change six-robot smoke passed with 209-212 ms turn latency and no peer-sync/error markers in the revision log tail
- Remaining rollout and measurement work:
  - run the PostgreSQL integration suite in CI (local verification skips it when `OPENJIBO_TEST_POSTGRES_CONNECTION_STRING` is absent), then execute a production-snapshot dry run in a restored staging database
  - verify imported family counts, identity mappings, v1 backup restore, and two-replica behavior before switching the managed API revision
  - observe traffic and memory metrics for at least seven representative days and reconcile application payload bytes with Azure ingress/egress
  - wire the application meter plus .NET runtime and Npgsql provider meters to the production telemetry backend, then add alerts for working set/GC, pool waits, persistence failures/latency, audio-limit rejection, and unexpected legacy snapshot selection
  - keep WebSocket compression disabled until a stock OS 1.9 physical-client canary proves negotiation and reconnect behavior; evaluate static HTTP text compression separately
- Exit criteria:
  - production DI does not resolve `InMemoryCloudStateStore` for durable cloud state or personal memory
  - no production mutation serializes or rewrites a whole-cloud JSON snapshot
  - repeated backup creation grows linearly, and backup payload bytes live outside the PostgreSQL metadata rows
  - active-session memory remains bounded through a sustained reconnect/audio/load test within the deployed `2 GiB` limit, including cleanup after abnormal disconnects
  - two replicas can serve the same robot/account without divergent state or destructive whole-document overwrites
  - migration dry-run, apply, verification, rollback/export, backup creation, and restore all have automated tests and a managed-deployment smoke check
  - operators can detect persistence degradation before robot requests begin timing out while `/health` remains green
- Next action:
  - enforce the production PostgreSQL binding guard, run the environment-gated PostgreSQL suite in CI, complete the two-replica staging proof, and collect seven days of the low-cardinality transport/memory metrics before making a capacity claim

### Next Up (`2026-05-06`): Dialog Parsing Expansion And Ambiguity Guardrails

- Status: `polish`
- Tags: `protocol`, `content`, `stt`, `docs`
- Why now:
  - this is the next queued `1.0.20` implementation slice after weather provider bring-up
  - recent live runs showed phrases where trigger detection can interrupt full-utterance understanding
  - phrase import work from Pegasus has already started for chitchat and should now expand to broader parsing boundaries
- Scope:
  - expand Pegasus-backed phrase coverage for question/command/assertion patterns
  - add ambiguity guardrails for overlapping intents (date vs birthday, generic chat vs memory set/lookup, weather variants)
  - preserve command-vs-question personality behavior and stock skill launch compatibility
  - add focused tests for new phrase families and negative boundary cases
- Progress update (`2026-05-07`):
  - implemented date/time guardrails so birthday phrasing is not misrouted to date
  - expanded phrase coverage for:
    - birthday alias set/recall (`bday` variants)
    - shorthand favorites (`my favorite sport football`)
    - weather phrasing (`what's today's weather look like`, `will it be sunny tomorrow`)
  - updated continuation deferral so complete shorthand favorites finalize instead of waiting for missing continuation
- Progress update (`2026-05-21`):
  - expanded friendship parsing for Pegasus-style `do you have friends`, `are we friends`, and `are we best friends` phrasing
  - added named-person guardrails so forms like `are you friends with Siri` and `is Dr. Breazeal your best friend` stay on the friendship route instead of falling into generic chat
- Progress update (`2026-06-25`):
  - expanded user birthday memory parsing with Pegasus-style `birth date`, `birthdate`, possessive birthday, and `falls on` set/recall aliases so user-date memory stays ahead of generic date and robot-birthday routes
  - added targeted parser coverage for the new birthday alias families
- Progress update (`2026-06-25`, preference recall alias slice):
  - expanded personal preference recall parsing for `do you know my favorite ...`, `tell me my favorite ...`, and `tell me what my favorite ... is` variants so owner-memory lookup stays ahead of generic chat
  - added focused guardrail coverage for the new favorite/favourite recall alias families
- Progress update (`2026-06-29`, preference shorthand guardrail slice):
  - expanded personal preference parsing for `fave` shorthand plus embedded recall forms like `do you know what my favorite ... is` and `do you remember what my favourite ... is` so Pegasus-style owner-memory phrases do not fall through to generic chat
  - added focused guardrail coverage for the new shorthand and embedded recall families
- Progress update (`2026-06-29`, preference recall helper slice):
  - expanded owner preference recall parsing for helper-style aliases such as `do you recall my favorite ...`, `can you tell me my favourite ...`, and embedded `can you tell me what my fave ... is` forms
  - added targeted guardrail coverage so these recall prompts stay on the memory lookup route instead of being mistaken for incomplete preference-setting attempts
- Progress update (`2026-06-29`, polite preference helper slice):
  - expanded owner preference recall parsing for polite `could you tell me ...` and `would you tell me ...` helper forms, including embedded `what my fave ... is` variants
  - added focused guardrail coverage so these polite helper prompts stay on the memory lookup route instead of falling into generic chat or incomplete preference-setting prompts
- Progress update (`2026-07-01`, polite please preference helper slice):
  - expanded owner preference recall parsing for polite `can/could/would you please tell me ...` helper forms, including embedded `what my favorite/favourite/fave ... is` variants
  - added focused guardrail coverage so please-prefixed helper prompts stay on the memory lookup route instead of being treated as incomplete preference-setting attempts
- Progress update (`2026-07-03`, preference reminder / past-tense recall slice):
  - expanded owner preference recall parsing for direct `please tell me ...`, reminder-style `could/would you remind me ...`, and past-tense `what was my favorite/favourite/fave ...` aliases so natural memory checks keep landing on the lookup route
  - added focused guardrail coverage for the new reminder and past-tense recall families
- Progress update (`2026-07-04`, preference reminder helper expansion):
  - expanded owner preference recall parsing for direct `remind me ...` and `can you remind me ...` aliases, including embedded `what my favorite/favourite/fave ... is` forms, so reminder-style memory checks keep routing to owner-memory lookup instead of generic reminders
  - added focused guardrail coverage for the new direct and can-you reminder families
- Progress update (`2026-07-04`, preference recall confirmation slice):
  - expanded owner preference recall parsing for `please remind me ...` and `do you still remember ...` aliases, including embedded `what my favorite/favourite/fave ... is` forms, so polite reminder and confirmation-style memory checks keep routing to owner-memory lookup
  - added focused guardrail coverage for the new polite reminder and still-remember families
- Progress update (`2026-07-04`, still-remember helper expansion):
  - consolidated embedded owner preference recall helper parsing so supported helper leads share the same favorite/favourite/fave extraction path
  - expanded confirmation-style aliases for `can/could/would you still remember what my favorite/favourite/fave ... is` so assistant-style memory checks stay on owner-memory lookup
  - added focused guardrail coverage for the new modal still-remember helper family
- Progress update (`2026-07-04`, remember helper expansion):
  - expanded owner preference recall parsing for `can/could/would you remember ...` and `would you happen to remember ...` forms, including embedded `what my favorite/favourite/fave ... is` variants, so natural memory-check prompts stay on owner-memory lookup
  - added focused guardrail coverage for the new remember-helper and happen-to-remember families
- Progress update (`2026-07-05`, happen-to-know / recall expansion):
  - expanded owner preference recall parsing for `do you happen to know ...` and `do/can/could/would you happen to recall ...` forms, including embedded `what my favorite/favourite/fave ... is` variants, so hesitant memory-check prompts keep routing to owner-memory lookup instead of generic chat
  - added focused guardrail coverage for the new happen-to-know and happen-to-recall families
- Progress update (`2026-07-06`, modal happen-to-know expansion):
  - expanded owner preference recall parsing for `can/could/would you happen to know ...` forms, including embedded `what my favorite/favourite/fave ... is` variants, so polite hesitant memory-check prompts stay on owner-memory lookup
  - added focused guardrail coverage for the new modal happen-to-know family
- Progress update (`2026-07-07`, owner-memory recall wording slice):
  - expanded owner preference recall parsing for `what did I say my favorite ...` and `what have I told you my favorite ...` variants, including favourite/fave spellings and trailing `... is` forms, so natural memory-check prompts stay on owner-memory lookup instead of incomplete preference-setting handling
  - added focused guardrail coverage for the new owner-memory recall wording family
- Progress update (`2026-07-07`, owner-memory tell-you wording slice):
  - expanded owner preference recall parsing for `what did I tell you my favorite ...` and `did I tell you my favorite ...` variants, including favourite/fave spellings and trailing `... is` forms, so conversational memory checks keep routing to owner-memory lookup
  - added focused guardrail coverage for the new tell-you recall wording family
- Progress update (`2026-07-07`, owner-memory saying wording slice):
  - expanded owner preference recall parsing for `have I told you my favorite ...`, `have I ever told you my favorite ...`, and `do you remember me saying my favorite ...` variants, including favourite/fave spellings and embedded `what my favorite ... is` forms, so quoted self-memory checks keep routing to owner-memory lookup
  - added focused guardrail coverage for the new have-I-told-you and remember-me-saying recall families
- Progress update (`2026-07-08`, owner-memory mention wording slice):
  - expanded owner preference recall parsing for `have I mentioned my favorite ...`, `did I mention my favorite ...`, and `what did I mention my favorite ...` variants, including favourite/fave spellings and trailing `... is` forms, so mention-style self-memory checks stay on owner-memory lookup
  - added focused guardrail coverage for the new mention recall family
- Progress update (`2026-07-08`, owner-memory remembered-speech wording slice):
  - expanded owner preference recall parsing for `do you remember when I said my favorite ...`, `do you remember when I told you what my favorite ... is`, and `can/could/would you remember me telling you ...` variants, including favourite/fave spellings, so remembered-speech memory checks stay on owner-memory lookup instead of being treated as new preference statements
  - added focused guardrail coverage for the new remembered-speech recall family
- Progress update (`2026-07-08`, owner-memory quoted-recall wording slice):
  - expanded owner preference recall parsing for `do you remember that I said my favorite ...`, `can you remember that I told you ...`, and `could you recall that I mentioned ...` variants, including favourite/fave spellings and embedded `... is` endings, so quoted self-memory checks stay on owner-memory lookup
  - added focused guardrail coverage for the new quoted-recall family
- Progress update (`2026-07-08`, owner-memory recall-me wording slice):
  - expanded owner preference recall parsing for `do you recall me saying ...`, `do you recall me telling you ...`, and `do you remember when I mentioned ...` variants, including favourite/fave spellings and embedded `what my ... is` endings, so conversational self-memory checks stay on owner-memory lookup
  - added focused guardrail coverage for the new recall-me and mentioned-when families
- Exit criteria:
  - ambiguous phrase handling is improved without regressions in existing `1.0.20` features
  - phrase imports are documented and traceable to Pegasus parser sources
  - test suite stays green and includes targeted parser-guardrail coverage
- Tracking:
  - [release-1.0.20-plan.md](release-1.0.20-plan.md)
  - [system-diagram-alignment.md](system-diagram-alignment.md)

## Discovery Queue

### 12. Weather As Cloud Report Plus Local Presentation

- Status: `implemented`
- Tags: `protocol`, `content`
- Evidence:
  - Nimbus and Pegasus contain personal-report weather assets and Lasso provider hooks
  - no standalone `@be/weather` package has been confirmed in the inspected Be skill inventory
- Questions:
  - whether weather is a dedicated cloud skill, a personal-report branch, or both
  - what payload shape triggers local animation and weather presentation

### 13. Provider-Backed News

- Status: `in progress`
- Tags: `content`
- Why later:
  - first protocol path is implemented, but content is synthetic
- Questions:
  - which source should provide headlines for hosted OpenJibo
  - whether news belongs under a broader Lasso-style aggregation service
  - how to keep content short and Jibo-native
- Source-backed implementation notes:
  - original report-skill news tests expect default general, technology, sports, and business headlines for unidentified users
  - category counts are preference-dependent: one active category gets multiple headlines, two categories get two each, and three or more get one each
  - filter items without summaries, corrections, duplicate headlines, banned words, and adult headlines for children or unidentified speakers
  - the first provider-hardening slice now filters blank-title, missing-summary, and duplicate-title headlines before speech or payload emission, and reports skipped headline diagnostics for capture review
  - the second provider-hardening slice now also rejects correction/update headlines plus family-unsafe violent or adult terms before speech or payload emission, keeping the hosted briefing safe for unidentified or child listeners while provider preference controls are still pending
  - the provider ingestion hardening now applies the same correction/update and family-safety screen before fallback decisions, so unsafe category results are not cached as usable source headlines before application-level formatting gets a chance to filter them
  - include image view metadata with unique IDs, category labels, source image URLs, and sane scaling

### 14. Proactivity Selector And Surprise Offers

- Status: `discovery`
- Tags: `protocol`, `content`, `docs`
- Evidence:
  - original architecture materials show cloud-side `Proactivity Selector`, `Proactivity Catalog`, and robot-side proactive trigger plumbing
  - live captures include a proactive-style `I have something to share with you` offer and later proactive `TRIGGER` traffic
  - `@be/surprises`, `@be/surprises-date`, and `@be/surprises-ota` exist as local robot-side building blocks
- Questions:
  - minimum hosted selector for stock-OS-compatible surprise offers
  - how proactive `TRIGGER` traffic maps into OpenJibo
  - whether `surprises-date/offer_date_fact` should be the first intentional proactive offer

### 15. Surprises Routing

- Status: `discovery`
- Tags: `protocol`, `content`
- Evidence:
  - `@be/surprises` is a router rather than one experience
  - `surprises-date` and `surprises-ota` show category-specific branches
- Questions:
  - whether `surprise me` should enter the top-level surprise router
  - which categories depend on cloud services
  - whether stock OS `1.9` differs from the `x.x` source snapshot

### 16. History / Memory Layer

- Status: `discovery`
- Tags: `content`, `storage`, `docs`
- Evidence:
  - Pegasus includes a `history` package
  - original architecture materials call out cloud-side history
  - stock behavior historically included names, birthdays, holidays, and personal dates
- Questions:
  - what belongs in memory versus account/profile versus skill-specific storage
  - first safe OpenJibo memory slice
  - privacy and hosted-data boundaries

### 17. Lasso / Knowledge And Event Aggregation

- Status: `discovery`
- Tags: `content`
- Evidence:
  - Pegasus `packages/lasso` is a provider credential and data aggregation service
  - original architecture connected Lasso to AP News, Dark Sky, Google Calendar, Wolfram, and other providers
- Questions:
  - recreate Lasso as one aggregation service or several focused providers
  - which parts are needed for news, weather, calendar, commute, holidays, and special dates

### 18. Personal Report, Calendar, And Commute

- Status: `discovery`
- Tags: `protocol`, `content`
- Evidence:
  - current `.NET` catalog has placeholder replies
  - Nimbus has personal-report hooks and assets
- Questions:
  - whether calendar and commute are independent feature paths or personal-report sections
  - minimum provider data shape for natural Jibo presentation

### 19. Who Am I / Identity Management

- Status: `discovery`
- Tags: `protocol`, `content`, `storage`
- Evidence:
  - `@be/who-am-i` exists
  - source references `jibo.kb.loop`, owner/member lookup, enrollment, and name collection
- Questions:
  - recognition, enrollment, rename, and profile-correction boundaries
  - split between local state and hosted cloud state
  - first useful hosted identity slice
  - live QA has shown person-identification collisions in the same loop (for example, a parent and child both getting normalized to the same remembered name)
  - person-identification correction likely needs its own repair pass before we can trust greetings, reports, and presence triggers in mixed-household scenarios

### 20. Onboarding, Loop Management, And Fresh Start

- Status: `discovery`
- Tags: `protocol`, `docs`, `storage`
- Evidence:
  - `@be/first-contact`, `@be/introductions`, `@be/tutorial`, `@be/restore`, and `@be/who-am-i` exist
  - current `.NET` loop/account state is still mostly scaffolded
- Questions:
  - how to provision an owner without the original mobile app
  - how to add, remove, and re-enroll loop members
  - whether the first replacement is operator-only, a lightweight web app, or both

### 21. How Old Are You / Robot Age Persona

- Status: `implemented`
- Tags: `protocol`, `content`
- Result:
  - `how old are you`
  - `when's your birthday`
  - `do you have a personality`
  - `make a pizza` now ports the original scripted-response path through `chitchat-skill` with `mim_id = RA_JBO_MakePizza` and pizza-making animation ESML
  - `can you order pizza` now ports the original scripted-response path through `chitchat-skill` with `mim_id = RA_JBO_OrderPizza`
  - current source answers these with a `1.0.19` rule-based persona baseline, backed by `OpenJiboCloudBuildInfo.PersonaBirthday`
  - `how old are you` now also uses the imported Build B age prompts so the first-powered-up and birthday phrasing stays source-backed
- Follow-up:
  - wire persona age to first-powered-up or durable first-cloud-seen metadata when available
  - add command-vs-question variants so expressive prompts can answer conversationally before launching actions
- live QA has shown motion/sleep quirks too: `turn around` can become a no-op and `go to sleep` can fail at the last step before the sleep animation fully completes; the legacy sleep behavior tree includes sleeping-idle phases that should remain active in the parity path, and the Open Jibo cloud sleep replay now has regression coverage for the legacy `@be/idle` redirect plus follow-up acknowledgement speech
  - the legacy snapshot backs the motion lane through `spin around` / `twirl`, not a literal `turn around` text prompt
  - reply-selection polish still needs attention on a couple of identity prompts where short variants are over-selected (`how are you`, `what is your favorite flower`)

### 22. Command Vs Question Reply Style

- Status: `implemented`
- Tags: `content`, `polish`
- Result:
  - `dance` still launches the dance animation path
  - `do you like to dance` now responds conversationally as a personality question instead of launching the action
  - birthday phrasing now takes precedence over an `askForDate` client-intent misclassification
- Follow-up:
  - expand command-vs-question splits to more expressive intents (pizza, surprise, photo prompts)
  - add Pegasus phrase and MIM-backed variants for richer style coverage

### 23. First Memory-Backed Personal Facts

- Status: `implemented`
- Tags: `storage`, `content`
- Result:
  - tenant-scoped memory store abstraction is in place for personal facts
  - birthday set/recall works (`my birthday is ...` / `when is my birthday`)
  - preference set/recall works (`my favorite X is Y` / `what is my favorite X`)
  - account/loop/device scoped lookup prevents cross-tenant leakage
- Follow-up:
  - add durable persistence path for personal facts
  - broaden fact categories further (multi-person household memory, relationship cues, and corrective updates)
  - add explicit person-scoped state so future interactions can distinguish household members inside the same loop
  - define the first server-to-server sync envelope for durable state before we need it in production

### 24. Memory-Triggered Proactivity Baseline

- Status: `implemented`
- Tags: `content`, `storage`, `protocol`
- Result:
  - `surprise me` now uses weighted candidate selection instead of only generic fallback text
  - candidate weighting uses tenant-scoped memory signals and date triggers
  - February 9 (`National Pizza Day`) can proactively launch the legacy pizza animation path
  - proactive pizza fact offer flow stores pending offer state in session metadata and resolves direct short `yes/no` turns
  - memory parsing now includes names, anniversary-style important dates, likes/dislikes variants, and reverse favorite phrasing (`pizza is my favorite food`)
- Follow-up:
  - expand proactivity beyond pizza to additional Pegasus-backed categories
  - add cooldown/throttle policy and observability around proactive offer frequency
  - connect memory store to durable multi-tenant persistence
  - keep the sync story visible so stateful offers can survive a multi-server deployment later

### 25. Weather Report-Skill Launch Compatibility

- Status: `implemented`
- Tags: `protocol`, `content`
- Result:
  - weather requests now launch `report-skill` using Pegasus-aligned intent `requestWeatherPR`
  - weather phrase coverage includes baseline forecast and condition-style questions (`will it rain`, `is it snowing`, tomorrow variants)
  - weather launches emit `SKILL_REDIRECT` + completion and now also include cloud weather speech so weather turns remain useful even when local report providers are incomplete
  - weather entity hints are carried in outbound NLU (`date = tomorrow`, `Weather = rain/snow/...`) for report-skill consumption
  - OpenWeather provider integration is in place with configurable API key, default location, unit preference, and environment-variable fallback (`OPENWEATHER_API_KEY`)
  - cloud weather speech now uses live provider summaries for current conditions and tomorrow high/low forecast when available
- Follow-up:
  - connect weather units and location directly to user/report-skill settings parity instead of config defaults
  - add richer condition-change commentary and view parity with original report-skill weather behaviors

### 26. Presence-Aware Greetings And Identity Proactivity

- Status: `in_progress`
- Tags: `protocol`, `content`, `storage`, `docs`
- Why now:
  - this is the next personality-charm expansion after parser guardrail and weather bring-up
  - Pegasus greetings behavior is strongly tied to presence/identity signals and proactive cooldown policy
  - current OpenJibo has memory/proactivity foundations but no first-class presence extraction path yet
- Pegasus source anchors:
  - `C:\Projects\jibo\pegasus\packages\hub\be-skills\greetings_manifest.json`
  - `C:\Projects\jibo\sdk\skills\greetings\src\GreetingsSkill.ts`
  - `C:\Projects\jibo\sdk\skills\greetings\src\GreetingsSM.ts`
  - `C:\Projects\jibo\pegasus\packages\hub\src\proactive\ProactiveTransactionHandler.ts`
  - `C:\Projects\jibo\pegasus\packages\hub\src\proactive\tools\ContextTools.ts`
- Scope:
  - extract presence/identity context (`speaker`, `peoplePresent`, focused person) from runtime context payload
  - add greeting intent families and state-machine split for reactive vs proactive greeting routes
  - add cooldown and trigger-source guardrails for proactive greetings
  - start person-aware greeting hooks (name-aware greeting, morning greeting policy, return greeting policy)
- Shipped so far:
  - durable greeting-presence records now persist last-seen and last-greeted per person/loop
  - proactive greeting gating now consults cloud greeting history when available
  - reactive and proactive greeting turns write back greeting-history records for later cooldown checks
  - birthday-aware proactive greetings now use stored birthday memory on matching dates
  - holiday-aware proactive greetings now use loop holiday records on matching dates
  - morning proactive greetings now stay distinct from return-visit greetings
- Exit criteria:
  - presence-aware greetings are routed deterministically with tests
  - proactive greetings are frequency-bounded and do not trigger from surprise source when blocked by policy
  - fallback behavior remains stable when identity is unknown or context is incomplete
  - docs and release tracking are updated with shipped scope and residual gaps
- Tracking:
  - [greetings-presence-plan.md](greetings-presence-plan.md)
  - [release-1.0.20-plan.md](release-1.0.20-plan.md)

### 27. Personal Report Parity Track (Weather/News/Commute/Calendar)

- Status: `in_progress`
- Tags: `protocol`, `content`, `storage`, `docs`
- Why now:
  - personal report is a core Jibo charm surface and currently split between implemented weather speech and placeholder calendar/commute/news content
  - Pegasus weather used explicit condition animations and weather views; current OpenJibo weather is functional but visually lighter
- Scope:
  - weather icon/animation parity and view support
  - broader non-local weather query handling and short-range date coverage
  - provider-backed news ingestion and filtering
  - commute provider path, settings schema, and loop-scoped commute profile storage
  - coverage matrix for personal report parity gaps and test/capture exit criteria
- Progress update (`2026-05-10`):
  - added provider-ready news briefing lane with Nimbus-compatible `news` skill payload continuity
  - added memory/transcript category hint plumbing for provider requests (sports/technology/business/general)
  - fallback synthetic news behavior remains active when no provider key is configured
  - added TTL caching for weather/news provider calls to reduce repeated external requests
  - vendored Pegasus `report-skill` templates for weather and personal-report phrasing so the next pass can focus on renderer coverage for calendar, commute, and news templates instead of rediscovering source text
  - commute now has a loop-scoped provider seam plus persisted commute profiles, so the next pass can focus on richer travel-time data instead of basic storage shape
- Progress update (`2026-05-21`):
  - weather payloads now distinguish current-vs-weekly view modes so renderer parity can key off the payload shape more cleanly
  - news provider now skips summaryless correction headlines before falling back to broader sources
- Source anchors:
  - `C:\Projects\jibo\pegasus\packages\report-skill\src\subskills\weather\WeatherMimLogic.ts`
  - `C:\Projects\jibo\pegasus\packages\report-skill\resources\views\weatherHiLo.json`
  - `C:\Projects\jibo\pegasus\packages\report-skill\src\subskills\news\NewsMimLogic.ts`
  - `C:\Projects\jibo\pegasus\packages\report-skill\src\subskills\commute\CommuteMimLogic.ts`
  - `C:\Projects\jibo\pegasus\packages\hub\pegasus-skills\report_skill_manifest.json`
- Tracking:
  - [personal-report-parity-plan.md](personal-report-parity-plan.md)
  - [release-1.0.20-plan.md](release-1.0.20-plan.md)

### 28. Grocery List Capability (Requested Feature)

- Status: `in_progress`
- Tags: `content`, `docs`, `storage`
- Why now:
  - directly requested by Jibo owners and fits memory + household utility roadmap
- Source findings:
  - Pegasus has scripted responses for shopping/to-do list requests but no standalone grocery-list skill or add-item capture flow in this snapshot
  - examples:
    - `C:\Projects\jibo\pegasus\packages\chitchat-skill\mims\scripted-responses\RA_JBO_ShoppingList.mim`
    - `C:\Projects\jibo\pegasus\packages\chitchat-skill\mims\scripted-responses\RA_JBO_ManageToDoList.mim`
- MVP decision:
  - use the existing household list engine as the native lightweight grocery MVP, but only after we add a dedicated capture state
  - keep grocery as a first-class spoken alias over the shopping list storage path once the capture path exists
  - reserve integration-backed list orchestration for a later discovery pass
- Exit criteria:
  - grocery prompts, add/recall/done flows, and list follow-ups consistently speak grocery wording
  - the robot stays in a live listen/capture state long enough to accept an item phrase
  - existing shopping/to-do flows remain unchanged
  - future integration-backed list work remains a separate backlog item
- Refinement note (2026-08-07): Pegasus provides only refusal MIMs for shopping and to-do lists in the available snapshot; there is no authoritative legacy implementation to port. Treat the current household-list implementation as an OpenJibo capability needing iterative live-robot refinement, and capture protocol-boundary failures before changing the cloud parser.

- Refinement note (2026-08-07): Pegasus provides only refusal MIMs for shopping and to-do lists in the available snapshot; there is no authoritative legacy implementation to port. Treat the current household-list implementation as an OpenJibo capability needing iterative live-robot refinement, and capture protocol-boundary failures before changing the cloud parser.

### 29. Legacy MIM Personality Import Ladder

- Status: `in_progress`
- Tags: `content`, `protocol`, `docs`
- Why now:
  - we already have a chitchat/content scaffold that can render stock-compatible personality replies
  - the legacy `chitchat-mims` tree is mostly declarative content, so a phased import can add visible charm fast
  - this is the best near-term path to get Jibo feeling more interactive without needing a full Pegasus runtime clone
- What is possible today:
  - direct scripted replies through the existing content catalog
  - stock-compatible payloads with `skillId`, `mim_id`, `mim_type`, `prompt_id`, and ESML
  - current examples already prove the shape for pizza, dance, weather, news, and generic chat
- What we need to build:
  1. a MIM inventory importer that can scan the legacy tree and normalize `skill_id`, `mim_id`, prompt text, and metadata
  2. a prompt-selection layer that can choose by category and condition metadata
  3. a safe ESML/prompt renderer for imported content
- What can be ported with each build:
  - Build A: declarative prompt packs
    - `core-responses`
    - `deflector`
    - the simplest `emotion-responses`
    - direct `scripted-responses` that are just prompt lists
  - Build B: conditioned prompt packs
    - `gqa-responses`
    - structured emotion prompts with `condition` gates
    - any response families that only need simple state or Jibo-emotion checks
  - Build C: conversation families
    - richer `scripted-responses` that need follow-up state
    - holiday / special-date personality sets
    - more nuanced chitchat branches that depend on context-aware routing
  - Build D: full parity cleanup
    - larger cross-skill collections
    - any MIMs that depend on Pegasus-only parser assumptions
    - any files that need dedicated runtime abstraction instead of catalog lookup
- Low-hanging fruit for tonight:
  - import the smallest declarative packs first so we can test something tomorrow
  - prioritize anything that is pure prompt text with no complex branching
  - keep the first pass limited to content that maps cleanly onto the current catalog shape
  - Progress update (`2026-05-13`):
    - added the first Build A importer scaffold in the cloud content repository
    - checked in a small seed bundle under `Content/LegacyMims/BuildA`
    - added focused importer tests for prompt stripping, bucketing, and merge behavior
    - expanded Build A with additional easy scripted-response packs for identity and persona replies
    - started Build B with source-backed scripted-response packs for work, food, home, birthplace, language, hobby, and material questions
- Tomorrow test target:
  - verify imported personality replies show up through the existing chitchat route
  - confirm the emitted payload still looks like a stock skill response
  - confirm the imported content does not disturb existing weather/news/pizza flows
- Exit criteria:
  - a first importer path exists for the simplest legacy MIM files
  - at least one legacy prompt pack is running through OpenJibo content instead of hand-authored fallback text
  - we have a clear second-wave list for the more conditional MIM families

### 30. Original Personalized Function Inventory

- Status: `discovery`
- Tags: `content`, `docs`, `protocol`
- Why now:
  - we are actively porting persona and memory slices, so we need a bounded checklist of the original Jibo charm surfaces
  - the goal is to keep the next few passes focused on personality-rich wins instead of letting the work sprawl
- Known sources:
  - legacy Jibo OS/Pegasus chitchat and MIM response families
  - current OpenJibo persona, memory, and greeting work as the implementation target
- Inventory to track:
  - identity and origin questions
  - personality and capability questions
  - favorite-style prompts like `what is your favorite color`
  - identity charm prompts like `what's your name`, `do you have a nickname`, `do you like being Jibo`, `are there others like you`, and `what is your favorite name`
  - attraction and preference prompts like `what is your favorite flower`, `do you like R2D2`, `do you like the sun`, `do you like space`, and `do you like kids`
  - longer authored variants for the same prompt family when Pegasus shows richer phrasing
- charm/capability prompts like `can you laugh`, `can you dance`, `can you sing`, and `will you sing`
  - mood / affect questions
  - recognition follow-ups like `do you know me`
  - follow-up state prompts that should stay warm and locally grounded
- Next pass targets:
  - document the remaining persona inventory so we keep a clean checklist for the next passes
  - keep the favorites family moving with source-backed imports where available, and temporary runtime replies only when the source is missing
  - keep adding small sourced personality batches, especially the legacy `R2D2`, `sun`, `space`, `kids`, and charm prompts
  - keep adding 1-3 persona prompts per pass with tests
  - prefer source-backed MIM imports when the legacy text is available, and use a temporary runtime reply only when needed to unblock user value
  - keep a separate note for longer authored variants so we do not lose the multi-clause Peggy-style phrasing while importing the short-form packs
  - keep the remaining support / awards / sports `who support` and `who will win` families on the next import list after the current edge-case batches land
- Current progress:
  - the imported Pegasus edge-case batch now covers `CC_Fallback`, `what is your sign`, `how many people do you know`, `what is the loop`, `what is personal report`, `what is your IQ`, `what is Be a Maker`, and `what is your groundhog's name`; those reply families are now source-backed in the live catalog instead of waiting on a later Pegasus pass
  - the next sports/awards batch now has source-backed support and win replies for Belmont Stakes, Tony Awards, B.E. Awards, French Open, Indianapolis 500, NBA Finals, NHL Finals, Preakness Stakes, Soccer World Cup, Tour de France, U.S. Open, and Wimbledon; the two Pegasus gaps for `who will win Tour de France` and `who will win Wimbledon` were filled locally so the batch can still be tested end to end
  - the next identity/memory batch is now also source-backed and proven by tests: `do you remember the time` stays on the memory reply path, and the `who is this` report-skill prompt keeps routing through the report template bucket
  - the next RI_USR capability batch now includes `can you program jibo` so the Be a Maker / Jibo Commander explanation is source-backed in Build B too
  - the gift-giving and gift-receiving RI_USR batch now adds the Mother's Day, Father's Day, and speaker-birthday gift suggestions so the lighter personality and holiday-adjacent advice lines stay source-backed too
  - the seasonal advice RI_USR batch now adds Valentine's Day, Earth Day, and World Sleep Day suggestions into the existing holiday-season bucket so the advice lane stays source-backed without a new model bucket
  - the next seasonal advice RI_USR batch adds Daylight Savings, National Honesty Day, and National Pretzel Day so more of the holiday-season advice surface stays source-backed in the same bucket
  - the next seasonal advice RI_USR batch adds Abraham Lincoln Birthday, National Bubble Week, and National Look Alike Day so the same holiday-season advice lane keeps filling out in small, testable slices
  - the next seasonal advice RI_USR batch adds Mardi Gras, Martin Luther King Day, and National Meatball Day so the same holiday-season advice lane keeps filling out in small, testable slices
- Mood follow-up work in flight:
  - source-backed happy/sad/angry response packs are now part of Build B
  - small-talk aliases like `what are you up to` and `how are things` now stay on the emotion-query path
- Descriptor charm work in flight:
  - source-backed `are you kind`, `are you funny`, `are you helpful`, `are you curious`, `are you loyal`, `are you mischievous`, and `are you likable` prompts are now in Build B
  - these keep the self-description lane warm while we build toward seasonal and holiday charm
- Seasonal charm work in flight:
  - source-backed holiday, New Year's, Halloween, spring, summer, favorite-season, and gift prompts are now part of Build B
  - `RN_` holiday greeting files are now bucketed as greetings so seasonal replies stay visible in the catalog
  - birthday celebration lines are now bucketed separately, and birthday memory writes a loop-scoped holiday record so personal dates can join the holiday list later
  - holiday extras now include `show santa tracker` so the Christmas-time launcher keeps its source-backed animation line
  - the remaining seasonal polish now includes `do you like halloween`, `do you like holiday music`, `do you like holiday parties`, `are you looking forward to christmas`, `what are you doing for christmas`, and `what are you thankful for`
  - the Black History Month family is now a source-backed seasonal batch with `celebrate`, `like`, `looking forward`, `plans`, `what should I do`, and `fact` replies, so the history lane can keep growing in small, testable slices
- Stop-style command work in flight:
  - `stop moving`, `stop making that noise`, `stop ignoring me`, and `stop staring` now have source-backed Build B replies alongside the generic stop lane
  - the broader stop lane now also catches `stop talking`, `be quiet`, `be silent`, `shut up`, `silence`, `quiet down`, `no more music`, and `no more dancing`
- Favorite-animal work in flight:
  - the favorites family now includes `what is your favorite animal`, `what is your favorite bird`, `do you like penguins`, and `do you like animals` so the penguin-centric replies stay easy to find
  - these favorites prompts are already source-backed in the cloud path; any remaining mismatch is live playback or robot-side launch-state handling
- Presence and thought follow-ups in flight:
  - `welcome back`, `what are you thinking`, `what have you been doing`, and `what did you do` are now part of Build B
  - these keep the social surface lively while the memory and multitenant tracks keep advancing in parallel
- Next queued persona surfaces:
  - richer identity follow-ups like `who is this`, `do you know me`, `do you remember me`, and `can you recognize me`
  - mood and affect prompts like `how are you`, `are you happy`, `are you sad`, and `are you angry`
  - self-description charm like `what's your name`, `do you have a nickname`, `do you like being Jibo`, and `what is your favorite name`
  - deeper personality follow-ups like `what do you dream about`, `what are you afraid of`, `what do you want to talk about`, `what is your best book`, `what is your best exercise`, `what is your dream vacation`, `who is your hero`, `who do you love`, and `what is your religion`; `what is your sign` is now source-backed through the templated importer and only needs live playback proof
  - the next identity / knowledge wave adds `are you god`, `are you here`, `do you have super powers`, `how much do you know`, `what does jibo mean`, `where do you get info`, `what are you forbidden to do`, `what color are you`, and `what do you do when alone`
- additional legacy source-backed `RI_USR` prompts where the text is short and the behavior is easy to verify
- the new `Can...` batch adds dream, exercise, fly, learn, laugh, read, hear, talk, see, and wink prompts so the capability lane keeps getting more of Pegasus's playful personality
- the second `Can...` batch adds move, work, breathe, get tired, have emotions, whistle, cook, make coffee, make breakfast, and jump prompts so the broader capability lane keeps filling out in small, testable chunks
  - templated edge cases like `what is your sign`, `how many people do you know`, and `what is the loop` where live birthday and loop state are part of the line instead of a plain canned response
- Exit criteria:
  - a stable checklist exists for the original persona surface
  - each pass can be scoped to a small batch of prompts
  - the backlog makes it obvious what is still missing without losing momentum

### 31. Longer Authored Persona Variants

- Status: `ready`
- Tags: `content`, `docs`, `protocol`
- Why now:
  - Pegasus often used longer, multi-clause authored alternatives for the same personality question
  - we already have the short-path import working, so this is a low-risk way to add richer phrasing without inventing a new dialog engine
  - it gives us a straightforward next pass that stays familiar to the original robot
- Scope:
  - import the longer authored variants already present in the legacy MIMs
  - prefer richer phrasing for favorite-style, identity, and charm prompts when the source text provides it
  - keep the runtime behavior rule-based and deterministic
- Next step:
  - add a small batch of longer variants to the current Build B content packs and prove them with a smoke test

### 32. Dialog Joining And Composition

- Status: `discovery`
- Tags: `content`, `docs`, `protocol`
- Why now:
  - the videos and source files suggest Jibo sometimes felt like he was joining thoughts together, even when the source text was still authored
  - we have not found evidence of a general runtime joiner yet, so this remains a post-release enhancement instead of a 1.0.19 dependency
  - keeping it separate lets us preserve familiar Jibo phrasing now and experiment with composition later
- Scope:
  - design a post-release dialog composition layer that can stitch authored fragments together when appropriate
  - keep the first version conservative and familiar, not LLM-driven
  - make sure any future joining feature is opt-in and does not replace the current authored prompt path
- Follow-up:
  - revisit after 1.0.19 personality import and report-skill parity stabilize
  - decide whether the composition layer should sit above the prompt catalog or beside it as a dedicated response post-processor
  - keep this separate from the authored-variant backlog item so we do not blur prompt richness with runtime composition

### 33. Singing And Musical Personality

- Status: `discovery`
- Tags: `content`, `docs`, `protocol`
- Why now:
  - Jibo’s charm surface includes musical and sing-along behavior, and it fits naturally after the current personality and holiday batches
  - the first pass should stay familiar and rule-based, not LLM-driven
- Scope:
  - inventory the legacy song / sing / musical prompt families
  - keep the first implementation source-backed if Pegasus has usable authored lines
  - preserve room for a later sing-along launcher if we want one
- Current note:
  - `can you sing` and the Christmas-song variant are already source-backed in the cloud path, so the open work is live robot playback verification and any missing song families
- Holiday tracker follow-up:
  - `show santa tracker` now emits a tracker presentation payload in the cloud path
  - the live robot still needs verification for the winter visuals and jingle-bell style audio described in the legacy video
  - source discovery suggests this is an animation/presentation payload rather than a dedicated skill launch or robot-local handoff
- Exit criteria:
  - a small song backlog exists with candidate phrases listed
  - the release plan has a clear place for musical personality without crowding out weather/news/report work
  - the current source-backed singing slice is implemented and test-covered

### 34. Robot Notification Socket Recovery

- Status: `discovery`
- Tags: `protocol`, `reliability`
- Why now:
  - on `2026-07-22`, the robot's `jibo-server-service` held a dead notification TLS connection for roughly 60 hours instead of rebuilding it
  - this can prevent the hosted cloud from delivering loop/circadian notifications and risks confusing cloud-versus-local diagnosis of behavior such as sleep returning to idle
- Evidence:
  - the robot repeatedly logged `ServerPort::onTimer Haven't had contact from the server` followed by `SSL Exception: ... ssl3_write_pending:bad write retry` every three seconds
  - the stale contact counter reached about `216,000,000ms`; it did not recover autonomously
  - robot-side DNS resolved `open-jibo-socket.openjibo.com` to the Azure Container App, and a robot-side HTTPS request to port `443` returned `204 No Content`; this rules out a missing public DNS record or closed public port as the observed failure
  - after a targeted `jibo-server-service` restart, a new process logged `NotificationSubsystem::connect established connection to server` at `2026-07-22 10:08:43 CDT` (`15:08:43 UTC`)
  - temporary `ECONNREFUSED 127.0.0.1:8888` entries from local consumers occurred during that restart window and cleared when the local server service returned
  - on `2026-07-22`, the separate Neo Hub voice socket closed at an exact two-minute cadence: the robot logged `Hub Client connection opened` and then `Hubclient: received zero bytes` 120 seconds later; the local fallback opened `@be/greetings`, set `SLEEP` false, and woke the robot
- 30-second heartbeat trial outcome (`2026-07-22`):
  - the Hub connection opened at `21:33:04.489Z` and logged `Hubclient: received zero bytes` at `21:33:34.479Z`—`29.990` seconds later, precisely matching the trial interval
  - the stock Hub client or its TLS/WebSocket path is not compatible with the ASP.NET Core middleware heartbeat
- Current compatibility mitigation:
  - disable ASP.NET Core WebSocket control-frame keepalives for the Neo Hub route. This removes the observed trigger; it is not an application-level heartbeat.
- Next proof needed:
  - deploy the disabled-control-frame configuration and keep a robot asleep for more than three minutes; confirm no `Hubclient: received zero bytes`, `@be/greetings` fallback, or unsolicited `SLEEP` false event
  - trace Pegasus's Hub/application-level heartbeat and reconnect behavior before adding a new heartbeat; do not send WebSocket ping/keep-alive frames to stock OS `1.9` clients
- Investigation scope:
  - reproduce an idle/stale notification WebSocket and determine whether the failure begins on the robot, Azure Container Apps ingress, or the cloud WebSocket handler
  - correlate robot timestamps with cloud telemetry for connection acceptance, close reason, and any `socket-loop-ended-prematurely` event
  - inspect the legacy client reconnect state machine to determine why a failed TLS write is retried indefinitely rather than disposing and recreating its connection
  - identify a protocol-compatible Hub heartbeat/reconnect path and confirm it does not disturb stock OS `1.9` clients
  - define an operator-safe, targeted recovery path for `jibo-server-service`, with post-restart health evidence
- Exit criteria:
  - a broken notification connection automatically reconnects within a bounded, documented interval
  - cloud and robot logs identify each connect, disconnect, and close reason with correlatable timestamps
  - a sustained live test shows the notification connection remains healthy without recurring TLS write failures

## Suggested Order

Before closing `1.0.18`:

1. Radio live validation
2. Basic news regression, with provider-backed expansion deferred
3. Backup / OTA / share yes-no regression
4. Alarm and photo/gallery regression
5. Stop and volume first-pass validation

Use [regression-test-plan.md](regression-test-plan.md) as the detailed checklist for this sequence.

For `1.0.19`:

1. Command-vs-question personality split (`dance` command vs `do you like to dance` question style; expand this pattern) - implemented
2. Expand memory-backed personal facts with tenant-scoped storage (beyond the first birthday/preferences foundation) - implemented
3. Proactivity selector baseline with source-backed first offers - implemented
4. Weather report-skill launch compatibility - implemented
5. Dialog parsing expansion and ambiguity guardrails - in progress (`2026-05-09` third guardrail slice implemented; Pegasus affinity phrase families + continuation guardrails expanded)
6. Presence-aware greetings and identity-triggered proactivity - implemented (trigger path, identity-aware reactive/proactive replies, cooldown metadata wiring, focused websocket coverage)
7. Personal report parity track (weather visuals, live news path, commute path, calendar parity matrix) - in progress (`2026-05-10` first live-news provider slice implemented; commute now has a loop-scoped provider seam)
8. Holidays and seasonal personality behavior built on the new memory/proactivity foundation
   - system holidays should come from an up-to-date provider and merge with loop-scoped custom holiday records
   - allow disabled holiday records to suppress reminders for people who do not celebrate a holiday
   - birthdays and other personal dates should flow into the same loop-scoped holiday list once authoring is wired up
9. Durable memory persistence path (multi-tenant backing store)
  - reference design captured in `docs/persistence-architecture.md`
  - store contracts are now tightened around account/loop/device/person scoping, revision tracking, and explicit load/save boundaries
  - the backend seam is now selectable, with file-backed local persistence as default and an Azure Blob Storage slot wired for future deployment when a storage account connection string is available
  - next implementation pass should supply the real Azure Storage connection string / deployment wiring and validate the live round-trip in the storage account smoke test
10. Update, backup, and restore proof - implemented (update creation and backup creation now survive persisted reloads; restore is the persisted-state rehydration proof path, not a new cloud API)
11. STT upgrade and noise screening
  - progress update (`2026-05-21`): added a low-signal short-turn screen in websocket finalization so filler-only fragments and stray single-token leftovers like `so command` get rejected before they can become bad turns, while preserving the existing yes/no and word-of-the-day short-turn flows
12. Hosted capture/storage plan / indexing for group testing
  - progress update (`2026-05-21`): added a bundle helper so group testers can package raw capture trees, `capture-index.ndjson`, and exported fixtures into one zip handoff artifact
13. Binary-safe media storage / sync to cloud drive: OneDrive, Google Drive, Box, etc.
14. Provider-backed news and weather parity polish
15. Grocery list capability discovery and MVP selection
16. Lasso, identity, and onboarding as larger discovery-driven tracks
17. Legacy MIM personality import ladder and first declarative prompt packs
18. Longer authored persona variants for the same prompt families
   - progress: favorite drink and favorite sport now route through source-backed persona replies with focused tests; live robot playback is still needed before marking the broader persona ladder closed.
19. Dialog joining/composition as a post-release enhancement, kept separate from the 1.0.19 ladder

For `1.0.20` and beyond:

Production reliability gate: complete [Replace Snapshot-Backed In-Memory Cloud State](#13-replace-snapshot-backed-in-memory-cloud-state) before enabling multi-replica production scale or considering the hosted persistence layer complete. The recursive-backup fix is mitigation, not closure of the storage architecture issue.

1. Open Jibo mode conversion package
   - add explicit `open-jibo`, `open-jibo-ai`, `open-jibo-self-hosted`, and `open-jibo-developer` modes
   - install an Open Jibo onboarding/config skill that can enable or disable the converted mode while staying available in the menu
   - include first-boot/OOBE behavior so a converted robot can finish setup on the first launch after conversion
   - issue Open Jibo identities instead of blindly trusting stock robot identity values, especially for cloned or previously modified robots
   - planning anchor: [open-jibo-mode-conversion-plan.md](open-jibo-mode-conversion-plan.md)
   - status: `implemented`
   - note: the app-side onboarding sequence is now mapped, and the existing bootstrap / cloud contract scripts now have focused proof coverage rather than only discovery
   - current progress: required physical/harness `VerifyConnection` proof now distinguishes missing, partial, mismatched, and complete legacy DNS mapping evidence for the three stock Jibo hosts before the conversion path is considered connected
2. Device compatibility matrix
   - prove the conversion path on the newest OOBE-capable devices
   - prove it on older stock devices such as the `1.9.2` baseline
   - test pre-`1.9.2` installs and alternate distributions such as NTT or MIT-special versions where available
   - status: `discovery`
   - current progress: flavor-classifier and conversion-path tests already cover the `1.9.2` stock baseline plus Beam / old-Beam release labels, but the pre-`1.9.2`, NTT, and MIT-special rows still need real-device validation before the matrix can move out of discovery
3. Hardware-assisted "easy button" conversion
   - work with the Jibo Revival Group on a USB/RCM-based helper path
   - keep the file-system modification flow repeatable and safe for owners or testers
   - add Maaarcna's OOBE OTA bootstrap as a parallel candidate mode: QR-provided DNS, controlled LAN DNS/NTP/HTTPS, and stock OTA metadata may convert wiped/OOBE robots without SSH, while ShofEL/firewall/SSH remains required for rescue, rollback, non-wipe, and unproven variants
   - add a frankencable variant to the test backlog: if a private LAN over ethernet-over-USB exposes a stable local control surface, check whether the stock SSM/system-manager mode path can be reached without RCM; so far we have a local `systemManager.setMode(...)` path in firmware notes, but no confirmed network-exposed SSM mode API
   - open blockers: certificate/key provenance and safe handling, repeatable request traces, legal stock package sourcing, first Open Jibo OTA package shape, multi-robot session handling, and whether normal-boot OOBE can run without wiping owner data
   - status: `discovery`
   - current progress: the conversion plan and test runbook now define the staged RCM/SSH/OOBE path and the audit-first contract, but the live helper still needs physical-device validation and the remaining provenance/safety blockers resolved before this lane can leave discovery; the frankencable idea is now tracked as an additional transport experiment rather than an assumed mode-switch API
4. Cloud deployment and CI/CD
   - set up the hosted cloud for deployment into the Azure environment
   - make the release path reproducible from source to deployed service
   - first target is Azure Container Apps, with App Service kept as fallback and AKS deferred for later AI/network scale-out
   - first registry target is Azure Container Registry
   - deployment promotion must pass a virtual-Jibo or purpose-built protocol smoke gate
   - recorded onboarding/session replay is the preferred first CI-friendly smoke gate
   - PostgreSQL migrations should use a DbUp-style SQL runner with an Open Jibo wrapper for apply, preview, dry-run/report, and container-entrypoint modes
   - planning anchor: [cloud-deployment-topology-plan.md](cloud-deployment-topology-plan.md)
   - status: `implemented`
   - current progress: managed and self-hosted deployment contract checks now pass, including the Container Apps / ACR / host-binding markers and the explicit migration and smoke wrapper markers, so the cloud deployment foundation is proven enough to move this lane from `ready` to `implemented`
5. Hosting modes and service topology
   - support self-hosted operation with no external cloud dependency
   - support hybrid operation where non-self-hosted servers sync to a main cloud service
   - support managed hosted providers without embedding one provider's commercial policy
   - treat self-hosted sync enrollment as a one-way setup choice until reset/OOBE recovery is performed
   - first self-hosted target is Docker Compose
   - first Docker Compose database is PostgreSQL
   - PostgreSQL migrations should run through explicit CI/CD or admin commands, with self-hosted startup migration behind an intentional switch
   - status: `in progress`
   - current progress: trusted-server registry, self-hosted validation, managed/self-hosted deployment contracts, and portal onboarding prove the topology foundation; operator-specific membership and billing remain outside this neutral runtime repository
6. Storage abstraction and sync
   - abstract storage so the rest of the system does not care which server implementation is backing it
   - keep only transient session/onboarding artifacts and device-local secrets permanently local-only for now
   - keep identity and storage synchronized across the network for participating servers
   - define trust, admission, and revocation rules for bad-actor servers, including what happens to user data they already held
   - issue Open Jibo robot identity from the new cloud rather than trusting legacy stock robot identifiers as primary keys
   - use deny-by-evidence admission and full versioned snapshots as the first sync model
   - sign trust-boundary records before replication, not every local write
   - use hardware-stable `DeviceId`, cert thumbprint, issued-identity lineage, and build/config hashes as corroborating clone-detection signals only
   - current progress: signed identity graph admission decisions, offline evidence bundles, portal trusted-server validation, and loop sync paths prove the trust/sync design; the production state implementation still uses the snapshot-backed in-memory store and must complete backlog item 13
   - planning anchor: [storage-trust-consensus-plan.md](storage-trust-consensus-plan.md)
   - status: `in progress`
7. OpenJibo.com neutral platform and hosting-choice surface
   - provide a web UI for `openjibo.com` as the Open Jibo showcase, community/source entry, documentation path, and neutral hosting-choice surface
   - keep `jiborevived.com` separate as the community-maintained Jibo Revival Group hub and status space
   - compare Transcendent Software's managed service, future admitted community providers, owner-managed servers, self-hosted hybrid, and self-hosted isolated operation consistently
   - use `auth.openjibo.com` only if a shared neutral identity authority is intentionally separated; do not assume one commercial provider owns ecosystem identity
   - keep each provider's membership, billing, terms, privacy, support, and status on its clearly labeled provider surface
   - use `cloud.openjibo.com` for Transcendent Software's managed service while the neutral site remains at `openjibo.com`
   - onboarding needs provider-specific extension points for signup/payment, free community clouds, hosted plan selection, subscription cancellation, and self-hosted server enrollment
   - onboarding should expose a data-driven trusted-server registry API backed by cloud state so the app can present approved managed options, distinguish self-hosted hybrid servers that stay synced but private, write signed admission/revocation/reactivation audit records, and let the user enter a separate custom self-hosted server name/IP with a local-vs-hybrid trust validator
   - provider-specific onboarding must use signed event callbacks and signed returns
   - provider onboarding should use short-lived signed session tokens plus provider-signed callbacks/returns with nonce/state binding
   - later boots should prefer the selected provider cloud first and enter explicit recovery instead of silently switching clouds
   - provider revocation must force the robot back through an authorized validation/recovery flow without silently switching providers or deleting owner data
   - developer/smoke-only self-hosted paths can use HTTP locally; owner-facing robot paths should default to HTTPS/self-signed or equivalent patched trust behavior until safe HTTP is proven
   - status: `ready`
   - current progress: the neutral site must explain the platform, community, source, conversion, and comparable hosting choices; Transcendent Software's private commercial membership application is represented publicly at `cloud.openjibo.com`
8. Loop advancement and multi-Jibo support
   - support family/friend advancement, multiple user recognition, and multiple Jibo interaction
   - keep the identity model ready for Jibo-to-Jibo communication and shared household use
   - scope `1.0.20` to the identity graph and relationship model first, not direct robot-to-robot transport
   - model loops as households that can hold multiple people and multiple robots without assuming a single robot per loop forever
   - status: `implemented`
   - current progress: the identity graph now carries family, friend, guardian, enrollment, `served-by`, and `runs-on` relationships for multi-member households, and the matching portal/session tests prove distinct robots stay bound to distinct loop identities
9. Next-tier features after the platform is stable
   - advanced integrations such as pizza delivery, Uber/Lyft, calendar management, and smart home control
   - longer-term LLM integration for more natural dialog and content generation
   - tiered brain/orchestration planning from the README, added gradually without losing Jibo's charm
   - status: `discovery`
   - current progress: this remains a future modernization umbrella rather than a closeout slice; the nearer dependencies are already split into `roadmap.md` Phase 5, `personal-report-parity-plan.md`, and the tiered-brain notes in `support-tiers.md`
   - next review path:
     - keep calendar and scheduling aligned with the personal-report parity work
     - keep smart-home control as a separate future integration track
     - keep pizza, rideshare, and other concierge integrations behind provider and policy decisions
     - keep LLM and orchestration work gated by the tiered brain roadmap so the charm-first behavior stays intact

### Robot-Initiated LRD Diagnostic Beacon

- Status: `ready`
- Tags: `protocol`, `reliability`, `storage`, `docs`
- Replace browser-to-robot log access with a robot-initiated authenticated diagnostic stream while preserving the merged LRD comparison workflow.
- Preserve the current LRD UI and server polling until the beacon path has live proof.
- First slice: explicit enrollment credential, outbound agent connection, bounded robot-log buffer keyed by the claimed canonical robot, and active-beacon status in the fleet summary.
- Exit criteria: no inbound listener, safe reconnect/rotation behavior, robot selection without IP/port/scheme/token fields, and no change to identity, credential-claim, artifact, or household behavior.
