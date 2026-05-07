# Feature Backlog

## Purpose

This backlog turns discovery into implementation slices for the hosted `.NET` cloud.

Use it as the working queue when picking the next feature or bug-fix slice. The release pattern is: implement a narrow slice, test it on stock OS `1.9`, update this file with what happened, then either close the release or roll the next larger idea forward.

The live regression checklist for release closeout is [regression-test-plan.md](regression-test-plan.md).

The active `1.0.19` execution shape is tracked in [release-1.0.19-plan.md](release-1.0.19-plan.md). This file keeps the full `1.0.18` evidence trail for parity reference.

Status key:

- `implemented`: present in current source and covered by focused tests
- `polish`: implemented enough to test, but still needs live proof or small cleanup
- `ready`: grounded enough to implement now
- `discovery`: more Pegasus, JiboOS, capture, or log work needed first
- `blocked`: waiting on infrastructure, provider choice, or a risky unknown

Tags:

- `protocol`: websocket, HTTP, or stock payload shape
- `content`: provider data or response content
- `docs`: operator docs, runbooks, or capture process
- `stt`: transcript reliability
- `storage`: persistence, media, backups, or hosted export

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
  - binary-safe media storage remains future work

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
  - `GetUpdateFrom` returns an empty object when no update is staged
  - staged updates can still be created explicitly
- Follow-up:
  - end-to-end update delivery and restore proof remains future work

## Near-Term `1.0.19` Queue

### 6. Stop Command

- Status: `polish`
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

### 7. Volume Up / Volume Down Voice Control

- Status: `polish`
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

### 8. Update, Backup, And Restore End-To-End Proof

- Status: `ready`
- Tags: `protocol`, `storage`, `docs`
- Why next:
  - prompt routing is improved, but lifecycle proof is still missing
- Current evidence:
  - `@be/settings` contains update and backup flows
  - `@be/restore` waits for a UGC key, runs restore, and reboots
  - original OTA surprise tests treat backup/download status as robot-local scheduler state, not as a direct cloud backup command path
  - no-op update fabrication has been removed from `.NET`
  - Test 25 still showed repeated backup-in-progress/update-menu blockage without `Backup_*` HTTP traffic
  - Test 26 repeated the backup-in-progress warning from robot-local `@be/surprises-ota` without `Backup_*` HTTP traffic
  - Test 27 repeated the same no-`Backup_*` finding and added evidence of local startup reconnect / `Q4-Server_connection_lost` before the test
  - Test 28 showed the same class of surprise handoff beginning at `@be/surprises` after Nimbus, before VAD inhibited the offer
- Exit criteria:
  - no phantom "always has updates" behavior
  - one controlled update can be staged and delivered
  - one controlled backup can be taken
  - restore behavior is documented well enough to recover a test robot intentionally

### 9. STT Upgrade And Noise Screening

- Status: `ready`
- Tags: `stt`
- Why next:
  - feature paths are now often correct when a transcript exists, but short replies and low-quality audio still block otherwise-correct flows
- Current evidence:
  - `jibo test 22` showed `ffmpeg` and `whisper.cpp` failures
  - `jibo test 23` did not show the same decode failure pattern, but gallery yes/no turns still produced empty ASR
  - `jibo test 24` still had collapsed or empty transcripts in alarm/gallery paths, including `Sudden alarm.`, `I'm setting alarm for seven.`, empty clock value input, and empty gallery preview input
  - `jibo test 25` still had short-answer failures, but several were cloud turn-state issues now patched rather than pure STT failures
  - `jibo test 26` had long no-`LISTEN` binary buffering and alarm-delete mishears now patched; remaining short-answer failures still need STT/noise work
  - current source now skips local whisper when buffered audio does not contain an Opus identification header
  - yes/no and alarm flows are especially sensitive to short or collapsed transcripts
- Implementation notes:
  - add lightweight waveform or energy screening before transcription
  - compare managed STT against the local toolchain
  - keep synthetic transcript hints for fixture replay

### 10. Hosted Capture And Storage Plan

- Status: `ready`
- Tags: `docs`, `storage`
- Why next:
  - repo-local captures work for single-operator testing, but group testing needs a cleaner archival/export boundary
- Implementation notes:
  - define local capture sinks versus hosted retention
  - decide how testers submit noteworthy sessions
  - preserve sanitized fixtures as the durable parity artifact

### 11. Binary-Safe Media Storage

- Status: `ready`
- Tags: `storage`, `protocol`
- Why next:
  - the first gallery bridge stores metadata and text-body placeholders, but final gallery support needs originals and thumbnails
- Questions:
  - whether stock gallery expects originals, thumbnails, or both
  - what upload metadata must survive for gallery refresh
  - how to map this cleanly to Blob Storage

### Next Up (`2026-05-06`): Dialog Parsing Expansion And Ambiguity Guardrails

- Status: `ready`
- Tags: `protocol`, `content`, `stt`, `docs`
- Why now:
  - this is the next queued `1.0.19` implementation slice after weather provider bring-up
  - recent live runs showed phrases where trigger detection can interrupt full-utterance understanding
  - phrase import work from Pegasus has already started for chitchat and should now expand to broader parsing boundaries
- Scope:
  - expand Pegasus-backed phrase coverage for question/command/assertion patterns
  - add ambiguity guardrails for overlapping intents (date vs birthday, generic chat vs memory set/lookup, weather variants)
  - preserve command-vs-question personality behavior and stock skill launch compatibility
  - add focused tests for new phrase families and negative boundary cases
- Exit criteria:
  - ambiguous phrase handling is improved without regressions in existing `1.0.19` features
  - phrase imports are documented and traceable to Pegasus parser sources
  - test suite stays green and includes targeted parser-guardrail coverage
- Tracking:
  - [release-1.0.19-plan.md](release-1.0.19-plan.md)
  - [system-diagram-alignment.md](system-diagram-alignment.md)

## Discovery Queue

### 12. Weather As Cloud Report Plus Local Presentation

- Status: `discovery`
- Tags: `protocol`, `content`
- Evidence:
  - Nimbus and Pegasus contain personal-report weather assets and Lasso provider hooks
  - no standalone `@be/weather` package has been confirmed in the inspected Be skill inventory
- Questions:
  - whether weather is a dedicated cloud skill, a personal-report branch, or both
  - what payload shape triggers local animation and weather presentation

### 13. Provider-Backed News

- Status: `ready`
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
- Follow-up:
  - wire persona age to first-powered-up or durable first-cloud-seen metadata when available
  - add command-vs-question variants so expressive prompts can answer conversationally before launching actions

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
5. Dialog parsing expansion and ambiguity guardrails - queued next as of `2026-05-06`
6. Holidays and seasonal personality behavior built on the new memory/proactivity foundation
7. Durable memory persistence path (multi-tenant backing store)
8. Update, backup, and restore proof
9. STT upgrade and noise screening
10. Hosted capture/storage plan / indexing for group testing
11. Binary-safe media storage / sync to cloud drive: OneDrive, Google Drive, Box, etc.
12. Provider-backed news and weather parity polish
13. Lasso, identity, and onboarding as larger discovery-driven tracks

For `1.0.20` and beyond:

1. Setup scripts to convert Jibo to Open Jibo by adding a mode for `open-jibo` pointing at our openjibo.com and `open-jibo-ai` pointing at openjibo.ai as a foundation for new cloud features and a clean separation from any remaining stock OS dependencies while preserving his original config
2. Setup scripts to put Jibo in `open-jibo` mode by default for new users, but allow existing users to keep the stock OS experience if they prefer by injecting a new skill that runs on startup to ask them if they want to convert to Open Jibo and switch modes, with a fallback timeout to switch modes automatically after a few weeks of inactivity (ensure new skill is accessible from menu so it can be opted into later on demand / likewise, if they have opted into Open Jibo, the skill will allow them to revert Jibo back to stock)
3. Setup openjibo.com and openjibo.ai domains with landing pages, support docs, and account management for future features that require hosted services or user accounts
4. Test Open Jibo with the new setup scripts and domains, and iterate on any issues that arise during the conversion process
5. Loop advancement (family and friends) / multiple user recognition / multiple Jibo support so Jibo's can interact and communicate
6. Advanced Jibo features such as pizza delivery, Uber/Lyft integration, calendar management, smart home control (Home Assistant), etc. can be added after the conversion process is smooth and stable, with a focus on features that leverage the new cloud capabilities and content personalization enabled by Open Jibo
7. LLM integration for more natural dialog, question answering, and content generation can be explored as a longer-term goal after the core platform is stable and has a growing user base to provide feedback and use cases for LLM-powered features
8. Tiered Jibo brain/orchestration plan from README.md can be implemented in parallel with the above, starting with the simplest cloud features and gradually adding more complex capabilities as the platform matures and user feedback is collected, always preserving his unique charm and original features.
