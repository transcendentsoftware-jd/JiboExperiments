# Feature Backlog

## Purpose

This backlog turns discovery into implementation slices for the hosted `.NET` cloud.

Use it as the working queue when picking the next feature or bug-fix slice. The release pattern is: implement a narrow slice, test it on stock OS `1.9`, update this file with what happened, then either close the release or roll the next larger idea forward.

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

## Current `1.0.18` Snapshot

Current cloud version: `1.0.18`

Runtime truth:

- hosted `.NET` projects and cloud tests target `net10.0`
- version source of truth is [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- `/health`, startup logging, and spoken `cloud version` are aligned with that constant

Current release theme:

- alarm and photo/gallery quirks have received the main bug-fix attention
- Word of the Day cleanup, constrained yes/no routing, unknown websocket event suppression, and local state persistence are already in the current code
- radio, ESML apostrophe cleanup, and first news are implemented in source/tests; radio and basic news are live-proven as of `jibo test 23`
- `jibo test 22` validated radio, exposed backup/load interference, exposed a shared yes/no no-input gap, exposed repeated create keeper prompts after photo handoff, and showed local whisper `ffmpeg` failures on unusable buffered audio
- `jibo test 23` validated basic news, proved one alarm set/fire path at `7:43 AM`, exposed comma-separated/short alarm follow-up parsing risk, showed stock alarm replacement yes/no rules that needed cloud handling, and showed photo gallery still failing when `shared/yes_no` ASR came back empty
- `jibo test 24` showed alarm replacement yes/no working, but exposed empty `clock/alarm_set_value` and `gallery/gallery_preview` turns falling into generic `I heard you` fallback speech; it also showed `CLIENT_NLU cancel` inside `clock/alarm_set_value` re-asking for an alarm value instead of closing the prompt

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
- Latest evidence:
  - `jibo test 22` did not show `Backup_*` HTTP traffic during the backup complaint
  - stock `@be/surprises-ota` drives the backup notification from robot-local `jibo.scheduler.backupStatus`
  - original `surprises-ota` tests make backup and OTA notifications contextual-priority prompts, with repeat suppression through last-notification timestamps
  - a spoken `take a backup` command currently routes as generic chat and is not the same as proving the local backup scheduler path
  - `jibo test 23` again showed backup-in-progress sluggishness and update-menu blockage while backups were active; explicit backup voice launch remains unwired
- Exit criteria:
  - spoken `yes` and `no` work on update, backup, share/offer, and gallery/create prompts
  - empty or missed short replies retry locally instead of relaunching Nimbus or generic chat
- Next action:
  - re-run these prompt families in the `1.0.18` live regression pass after the shared yes/no, alarm yes/no, and create no-input fixes
  - keep explicit backup creation as part of the update/backup/restore proof slice, not as an assumed yes/no prompt test

### 4. Alarm And Photo Gallery Release Regression

- Status: `polish`
- Tags: `protocol`, `stt`
- Why now: this is the main bug-fix theme for `1.0.18`.
- Current code:
  - alarm values parse explicit, compact, spaced, comma-separated, hyphenated, and local-context ambiguous times
  - short alarm/timer value replies are accepted during clock value follow-up rules instead of being filtered out before parsing
  - empty alarm/timer value turns complete locally as no-input instead of falling through to generic Nimbus speech
  - missing alarm times stay in local `@be/clock` clarification
  - alarm cancel can reuse the last active clock domain
  - cancel inside a clock value prompt maps to local clock `cancel`
  - stock alarm replacement/no-alarm prompts use the constrained yes/no path
  - gallery opens as `@be/gallery`; snapshot and photobooth open through `@be/create`
  - empty `gallery/gallery_preview` turns complete locally as no-input instead of relaunching Nimbus fallback speech
  - passive gallery/create context no longer reopens stale cloud turns
  - `shared/yes_no` no-input fallback and repeated create keeper cleanup were added after `jibo test 22`
- Latest evidence:
  - gallery opened and handed into create, but repeated `create/is_it_a_keeper` prompts could leave the blue ring/listening state
  - alarm recognition collapsed several attempts before a complete alarm value could be set
  - `ffmpeg` failures were present during the same test window, so alarm/gallery retest should separate transcript quality from payload shape
  - `jibo test 23` set and fired a `7:43 AM` alarm, then failed a later clarify/replacement path when the robot heard `- Time. - 7, 14.` and stock NLU converted that to `7:00 PM`
  - `jibo test 23` photo gallery got stuck on `shared/yes_no` turns with empty ASR, not on a transcript-bearing `yes` that the cloud mapped incorrectly
  - `jibo test 24` recognized `Yes.` for `clock/alarm_timer_change`, but empty `clock/alarm_set_value` produced `I heard you`; current source now keeps that as local no-input
  - `jibo test 24` showed photo/gallery blue-ring cleanup improved and create keeper completion working, but empty `gallery/gallery_preview` produced `I heard you`; current source now keeps that as local no-input
  - original clock tests confirm cancel inside the alarm value prompt must close without scheduling, existing-alarm `keep` must preserve KB/scheduler state, and existing-alarm `delete` or `cancel` must clear it
  - original gallery tests confirm empty-gallery `yes` redirects to `@be/create`, empty-gallery `no` exits, media-load failure exits, and delete confirmation only deletes on a positive `yes`
- Exit criteria:
  - gallery opens, offers to take a picture if empty, accepts `yes`, and hands into create
  - alarm set, clarify, replacement yes/no, cancel from value prompt, and cancel/delete flows behave locally and agree with the menu state
  - alarm replacement and deletion regression checks verify both websocket payload shape and persistent robot menu state where possible
  - failures caused by collapsed STT transcripts are logged as STT issues rather than misdiagnosed as payload bugs
- Next action:
  - re-run a stock OS `1.9` regression bundle before declaring `1.0.18` complete

### 5. Optional Small Feature Before `1.0.18` Freeze

- Status: `ready`
- Tags: `protocol`
- Why now: the user wants one or two features before `1.0.18` is called complete, but the release should not take on a risky subsystem.
- Preferred candidates:
  - Stop command
  - Volume up / volume down voice control
  - How old are you / robot age persona
- Guidance:
  - pick only one if the live regression pass finds bugs
  - pick at most two if the current bug-fix paths stay stable
  - keep the implementation source-backed and easy to revert or defer

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

### Photo / Gallery / Create Family

- Status: `implemented`
- Tags: `protocol`, `storage`
- Result:
  - gallery, snapshot, and photobooth voice paths route to the correct local skills
  - media metadata persists locally
  - `/media/{path}` serves the current text-body placeholder payload
  - empty `gallery/gallery_preview` turns produce local no-input instead of generic Nimbus fallback speech
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

- Status: `ready`
- Tags: `protocol`
- User goals:
  - `stop`
  - `stop that`
  - `never mind`
- Evidence:
  - `@be/idle` exists and is already used as a cleanup redirect target
- Questions:
  - whether stock source has a dedicated stop/cancel intent beyond idle redirect
  - whether stop should interrupt active local skills or only cloud speech paths in the first pass
- Exit criteria:
  - a spoken stop command settles the robot locally without a generic chat reply

### 7. Volume Up / Volume Down Voice Control

- Status: `ready`
- Tags: `protocol`
- User goals:
  - `turn it up`
  - `turn it down`
  - `increase the volume`
  - `decrease the volume`
- Evidence:
  - stock Jibo exposes volume control through robot UX, so there should be a local control or settings path to mirror
- Questions:
  - exact local payload shape for relative volume changes
  - whether first pass should support absolute values such as `set volume to 5`
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

- Status: `discovery`
- Tags: `protocol`, `content`
- User goals:
  - `how old are you`
  - answer from stored first-powered-up or first-cloud-seen metadata
  - optional zodiac/personality flavor when available
- Questions:
  - where stock Jibo stores first-power-up or birthdate metadata
  - whether a stock persona path exists
  - whether first OpenJibo pass should use first-cloud-seen metadata if stock data is unavailable

### 22. Command Vs Question Reply Style

- Status: `ready`
- Tags: `content`, `polish`
- User goals:
  - `dance` should behave like a willing action
  - `do you like to dance` should answer the question before or instead of treating it like the same command
- Implementation notes:
  - evolve reply collections into command/question variants
  - start with dance or another expressive skill
  - keep the first version rule-based

## Suggested Order

Before closing `1.0.18`:

1. Radio live validation
2. Basic news regression, with provider-backed expansion deferred
3. Backup / OTA / share yes-no regression
4. Alarm and photo/gallery regression
5. Optional small feature only if the regression pass stays calm

For `1.0.19`:

1. Stop command or volume control
2. Update, backup, and restore proof
3. STT upgrade and noise screening
4. Hosted capture/storage plan
5. Binary-safe media storage
6. Provider-backed news or weather
7. Proactivity, memory/history, Lasso, identity, and onboarding as larger discovery-driven tracks
