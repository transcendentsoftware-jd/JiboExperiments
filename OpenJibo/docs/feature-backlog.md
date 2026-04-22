# Feature Backlog

## Purpose

This backlog turns the current discovery work into a concrete implementation queue for the hosted `.NET` cloud.

Use it as the source of truth for the next feature slice instead of continuing the same investigation in chat each time.

## How To Use This Backlog

1. Pick one slice.
2. Confirm the target payload shape from captures and robot source.
3. Implement the smallest working parity path in `.NET`.
4. Test it live on stock OS `1.9`.
5. Update this file with results, regressions, and next guesses before moving on.

Status key:

- `ready`: grounded enough to implement now
- `discovery`: more robot-source or capture work needed first
- `polish`: behavior exists but needs cleanup

Parallel tags:

- `protocol`: websocket / turn-shape work
- `content`: provider or cloud content work
- `docs`: runbook / operator guidance
- `stt`: transcript reliability work

## Immediate Queue

### 1. Radio Resume And Genre Launch

- Status: `ready`
- Tags: `protocol`
- Why now: `@be/radio` is a real local skill and is the clearest low-risk expansion after Word of the Day.
- User goals:
  - `open the radio` should resume the current or last station
  - `play country music` should open a country station on iHeartRadio
- Current evidence:
  - [index.js](C:/Projects/JiboOs/V3.1/build/opt/jibo/Jibo/Skills/@be/be/node_modules/@be/radio/index.js) resumes from `lastStation`
  - the same file treats `menu` as a `play` launch and reads `result.nlu.entities.station`
  - the same file confirms `menu + no station` is the clean resume path and `menu + station=Country` becomes a direct genre launch
- Implementation notes:
  - add phrase routing for radio open/resume and genre launch
  - inspect radio genre and station metadata before locking the outbound entity values
  - prefer the same payload shape the menu path uses instead of a generic cloud speech reply
- Exit criteria:
  - voice `open the radio` launches radio successfully
  - voice `play country music` launches a country station
  - no fallback cloud placeholder reply is spoken on success

### 2. ESML Apostrophe Encoding Bug

- Status: `ready`
- Tags: `polish`
- Why now: this is a small, high-confidence speech quality bug affecting many paths.
- Current evidence:
  - [ResponsePlanToSocketMessagesMapper.cs](C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/ResponsePlanToSocketMessagesMapper.cs) currently escapes `'` to `&apos;`
  - the robot is pronouncing the encoded form instead of treating it as natural text
- Implementation notes:
  - stop encoding apostrophes in spoken ESML text unless a capture proves a narrower escaping rule is needed
  - keep escaping for `&`, `<`, and `>`
- Exit criteria:
  - contractions and possessives sound natural again in live speech

### 3. Backup / OTA Yes-No Reliability

- Status: `ready`
- Tags: `protocol`, `stt`
- Why now: the update and backup prompts are real daily-use system flows and still feel fragile.
- Current evidence:
  - `surprises-ota` is a real robot-side skill family in [index.js](C:/Projects/JiboOs/V3.1/build/opt/jibo/Jibo/Skills/@be/be/node_modules/@be/surprises-ota/index.js)
  - we already improved constrained yes-no routing, but live tests still show some turns collapse into empty transcript or generic speech
- Implementation notes:
  - keep local rules only on constrained replies
  - improve empty-turn retry behavior for settings and OTA prompts
  - capture whether stock OS uses a different yes-no prompt shape in backup versus update flows
  - investigate why the current cloud wiring appears to make the robot think updates are constantly available
- Exit criteria:
  - spoken `yes` and `no` reliably work on backup and update prompts
  - empty or missed turns retry locally without relaunching Nimbus

### 4. Proactive Share / Offer Yes-No Reliability

- Status: `ready`
- Tags: `protocol`, `stt`
- Why now: the latest capture bundle shows a second yes-no family where the robot asks whether it can share something, and spoken `yes` is still being handled like unconstrained speech instead of a reply to the active prompt.
- Current evidence:
  - the attached `jibo test 13` session includes both examples in one bundle:
    - a proactive or share-style prompt where spoken `yes` was treated as generic speech
    - a later update prompt where spoken `no` was accepted correctly
  - the share prompt uses `surprises-date/offer_date_fact` with `$YESNO`, and the failing reply leaked `globals/*` rules back into a Nimbus relaunch
- Implementation notes:
  - compare the active listen rules, ASR hints, and local skill ownership for the share-style prompt versus OTA prompts
  - make constrained yes-no detection cover this prompt family without regressing the already-working update `no` path
  - prefer local retry or local completion behavior over falling back into generic chat or Nimbus
- Exit criteria:
  - spoken `yes` and `no` work on share / offer prompts with the same reliability as the OTA path
  - constrained yes-no handling is generalized by prompt family instead of hard-coded only for updates

## Near-Term Queue

### 5. News Through Nimbus / Personal Report

- Status: `ready`
- Tags: `protocol`, `content`
- Why now: Nimbus already exposes a `news` cloud hook, so this is the next best cloud-first skill after radio.
- Current evidence:
  - [ProcessCloud.ts](C:/Projects/JiboOs/V3.1/build/opt/jibo/Jibo/Skills/@be/be/node_modules/@be/nimbus/src/states/ProcessCloud.ts) checks for `cloudSkill === 'news'`
  - Nimbus analytics and assets also reference `personal-report`
- Implementation notes:
  - decide whether the first pass is a simple headline summary or a closer personal-report style payload
  - confirm whether stock OS expects `news` as a dedicated cloud skill or under the broader personal-report family
- Latest progress:
  - first pass should use Nimbus's supported cloud path by setting `match.cloudSkill = news` and returning a supported `SLIM` announcement
  - provider-backed headlines can follow later under the `Lasso / Knowledge And Event Aggregation` track
- Exit criteria:
  - `tell me the news` reaches a non-placeholder live path
  - robot behavior feels Nimbus-native rather than generic chat playback

### 6. Clock Family Audit

- Status: `in_progress`
- Tags: `protocol`
- Why now: clock, date, timer, and alarm menu hooks are already visible in captures and the robot repo has a real `@be/clock` skill.
- Current evidence:
  - [protocol-inventory.md](C:/Projects/JiboExperiments/OpenJibo/docs/protocol-inventory.md) already tracks menu intents for `askForTime`, `askForDate`, `timerValue`, and `alarmValue`
  - `@be/clock` exists in the robot skill inventory
  - `JiboOs` shows `@be/clock` branches on `entities.domain = clock | timer | alarm`, uses `intent = menu` for menu launches, and has distinct local value-collection rules such as `clock/timer_set_value`
  - [artifact-output/jibo-test-15](C:/Projects/JiboExperiments/artifact-output/jibo-test-15) shows stock OS 1.9 rejecting our older `timerValue` top-level launch with `found no matching transition`, which points to a stock-style `start` flow plus local follow-up value rules instead
  - Implementation notes:
    - compare our custom time/date path against actual menu payloads
    - keep direct clock/date/day local, but treat timer and alarm as a two-part flow: stock start intent plus bare follow-up parsing on `clock/*_set_value`
  - decide whether timer and alarm should stay robot-local with cloud acknowledgement, or whether cloud needs to shape the launch and follow-up turns
- Progress so far:
  - voice `open the clock` now routes to the direct local `askForTime` clock-view path instead of the broader clock menu
  - voice `what time is it`, `what's today's date`, and `what day is it` now use stock-shaped local `@be/clock` handoffs instead of custom cloud-only speech
  - voice `set a timer for five minutes`, `set an alarm for 7:30 am`, `set an alarm for 830`, and `set an alarm for 8 30` now emit direct `timerValue` / `alarmValue` payloads with the entities the local skill expects
  - partial timer/alarm requests such as `set a timer` and `set an alarm` now stay on a controlled clarification reply path instead of drifting into Nimbus/chat echo
- Exit criteria:
  - time/date behavior stays correct
  - timer and alarm launch or set correctly from both menu and voice where applicable

### 7. Photo Family Audit

- Status: `in_progress`
- Tags: `protocol`, `docs`
- Why now: photo confirmation improved already, and the robot skill inventory includes `gallery`.
- Current evidence:
  - `@be/gallery` exists in the robot skill inventory
  - current captures already show `snapshot` and related menu destinations
  - `JiboOs` shows `@be/gallery` opens from `intent = menu`, while `snapshot` and `photobooth` actually map into `@be/create` with `createOnePhoto` and `createSomePhotos`
- Implementation notes:
  - separate three flows:
    - snap a picture
    - photo gallery
    - photobooth
  - document whether each one is local-only, cloud-assisted, or upload-backed
- Progress so far:
  - voice `open photo gallery` now launches local `@be/gallery` with a stock-shaped `menu` handoff
  - voice `snap a picture` now launches local `@be/create` with `createOnePhoto`
  - voice `open photobooth` now launches local `@be/create` with `createSomePhotos`
  - media and update metadata now persist to a local state file in the hosted `.NET` path, so gallery and staged update state are no longer strictly process-memory-only
  - `Media.Create` now retains uploaded metadata plus a best-effort raw body placeholder and serves the same media URL back through `/media/{path}`
- Open questions:
  - whether stock Jibo treats captured media as a short-lived local cache until cloud upload completes
  - what binary upload path and metadata are needed so gallery content persists instead of aging out locally
  - whether hosted OpenJibo should store originals, thumbnails, or both
  - whether the current lossy HTTP body capture is enough for stock gallery thumbnails, or whether we need a binary-safe upload persistence path next
- Exit criteria:
  - known photo menu and voice phrases map to the correct local path
  - capture storage expectations are documented for laptop versus hosted testing

### 8. Update, Backup, And Restore End-To-End Proof

- Status: `ready`
- Tags: `protocol`, `docs`
- Why now: prompt routing is only part of the lifecycle; we still need to prove a realistic maintenance and recovery story.
- Current evidence:
  - `@be/settings` contains update flows and explicit `jibo.kb.loop.hasKeyBackup(...)` checks for key-backup state
  - `@be/restore` is a dedicated local skill that waits for a UGC key, runs `jibo.systemManager.restore(...)`, and reboots on completion or failure
  - live behavior suggests the current cloud may be advertising updates too eagerly, leaving the robot thinking updates are always pending
- Implementation notes:
  - inspect how OpenJibo advertises update manifests so the robot does not repeatedly think an update exists when nothing meaningful is pending
  - prove one successful backup path, one successful update delivery path, and one successful restore path
  - document the operator steps, risk boundaries, and recovery expectations before broader rollout
- Latest progress:
  - unstaged update queries no longer fabricate a placeholder no-op manifest, which should reduce the phantom `always has updates` behavior during normal operation
  - real staged updates can still be created explicitly through the protocol layer when we are ready to prove end-to-end delivery
- Exit criteria:
  - no phantom "always has updates" behavior in normal operation
  - one controlled update can be delivered successfully
  - one controlled backup can be taken successfully
  - restore behavior is understood and documented well enough to recover a test robot intentionally

## Discovery Queue

### 9. Weather As Cloud Report Plus Local Presentation

- Status: `discovery`
- Tags: `protocol`, `content`
- Why later: there is strong evidence for weather assets under Nimbus, but not for a standalone local skill package.
- Current evidence:
  - Nimbus assets include personal-report weather content
  - no standalone `@be/weather` package is present in the inspected Be skill inventory
- Questions to answer:
  - is weather a dedicated cloud skill, a personal-report branch, or both
  - what payload shape triggers the local animation / embodiment layer
  - whether the first pass should be cloud speech only or forecast plus presentation metadata

### 10. Proactivity Selector And Surprise Offers

- Status: `discovery`
- Tags: `protocol`, `content`, `docs`
- Why later: the original architecture and recent proactive captures suggest proactivity is a first-class cloud subsystem, not just ordinary chat that starts itself.
- Current evidence:
  - the attached original Jibo architecture diagram shows a cloud-side `Proactivity Selector`, `Proactivity Catalog`, and robot-side proactive trigger plumbing
  - [jibo test 13.txt](C:/Projects/JiboExperiments/artifact-output/jibo-test-13/jibo%20test%2013.txt) and its websocket artifacts show a proactive-style `I have something to share with you` offer and later proactive `TRIGGER` traffic
  - `@be/surprises`, `@be/surprises-date`, and `@be/surprises-ota` already exist as local robot-side building blocks
- Questions to answer:
  - what minimum cloud-side selector we need for stock-OS-compatible surprise offers
  - how proactive `TRIGGER` traffic should map into a hosted OpenJibo proactivity service
  - whether `surprises-date/offer_date_fact` should be the first end-to-end proactive offer we intentionally support
- Implementation notes:
  - model proactivity as its own orchestrator separate from ordinary conversational turn routing
  - include offer, constrained yes/no, fulfillment, and dismissal behavior in the design
  - preserve the artifact linkage to the original architecture diagram and `jibo-test-13`

### 11. Surprises Routing

- Status: `discovery`
- Tags: `protocol`, `content`
- Why later: `@be/surprises` is a router, not a single experience, so we should not wire this blindly.
- Current evidence:
  - [SurpriseSkill.ts](C:/Projects/JiboOs/V3.1/build/opt/jibo/Jibo/Skills/@be/be/node_modules/@be/surprises/src/SurpriseSkill.ts) selects among surprise categories
  - `surprises-date` and `surprises-ota` show category-specific branches already exist
- Questions to answer:
  - should `surprise me` enter the top-level surprise router
  - which categories still depend on cloud services versus fully local logic
  - whether stock OS `1.9` differs materially from the `3.1` source snapshot here

### 12. History / Memory Layer

- Status: `discovery`
- Tags: `content`, `docs`
- Why later: the original architecture explicitly calls out `History`, and that likely maps to the kind of durable personal memory we want for names, preferences, and remembered facts.
- Current evidence:
  - the attached original Jibo architecture diagram includes a dedicated `History` component in cloud storage
  - stock Jibo behavior historically included awareness of names, birthdays, holidays, and special dates
- Questions to answer:
  - what data belongs in memory versus account/profile versus skill-specific storage
  - how much of the original behavior was robot-local versus cloud-backed
  - what the first safe OpenJibo memory slice should be
- Implementation notes:
  - plan for person identity, preferred name, birthday, relationship facts, and notable dates
  - keep the first design privacy-aware and easy to host
  - treat this as shared infrastructure that other skills can consume rather than a standalone feature

### 13. Lasso / Knowledge And Event Aggregation

- Status: `discovery`
- Tags: `content`
- Why later: the original architecture diagram suggests `Lasso` sits between the hub and outside data sources, which likely explains how Jibo knew about news, calendar items, holidays, and other structured world events.
- Current evidence:
  - the attached original Jibo architecture diagram shows `Lasso` connected to 3rd-party data such as AP News, Dark Sky, GCalendar, Wolfram, and other external sources
  - stock Jibo behavior historically covered holidays, birthdays, special events, and topical knowledge
- Questions to answer:
  - whether `Lasso` should be recreated as a single aggregation service or as several focused providers behind a shared interface
  - which parts are needed for news, weather, calendar, commute, astrology/date facts, and holidays
  - what subset is practical for a hosted OpenJibo v1
- Implementation notes:
  - treat holidays and special dates as first-class backlog scope here
  - use this item to drive future provider work for news, weather, calendar, commute, and event awareness

### 14. Personal Report, Calendar, And Commute

- Status: `discovery`
- Tags: `protocol`, `content`
- Why later: these are already stubbed in `.NET`, but the robot-side ownership still needs clearer mapping.
- Current evidence:
  - current `.NET` placeholders live in [InMemoryJiboExperienceContentRepository.cs](C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Content/InMemoryJiboExperienceContentRepository.cs)
  - Nimbus has personal-report hooks, but the exact cloud contract still needs confirmation
- Questions to answer:
  - should calendar and commute be independent feature paths or sections inside personal report
  - what minimum provider data shape lets Jibo present these naturally
  
### 15. Who Am I / Identity Management

- Status: `discovery`
- Tags: `protocol`, `content`, `docs`
- Why later: there is a real local `@be/who-am-i` skill, which likely covers user identification, name capture, and enrollment cues that matter for a modern identity layer.
- Current evidence:
  - `@be/who-am-i` exists in the stock skill inventory
  - the skill source references `jibo.kb.loop`, loop owner / loop member lookup, enrollment state, hypothesis views, and a `Who Am I_ Collect Name` flow
- Questions to answer:
  - whether `who am I` is primarily recognition, enrollment, or profile correction
  - how name, face, and voice enrollment were originally split between robot-local state and cloud services
  - what the minimum hosted-cloud contract is to make identity feel native again
- Implementation notes:
  - tie this work back to the broader `History / Memory Layer`
  - capture whether the first useful slice is recognition-only, rename-only, or full enrollment support

### 16. Onboarding, Loop Management, And Fresh Start

- Status: `discovery`
- Tags: `protocol`, `docs`
- Why later: stock Jibo onboarding and household management were app-driven, and a hosted OpenJibo path will need a replacement for adding/removing people and setting ownership cleanly.
- Current evidence:
  - `@be/first-contact`, `@be/introductions`, `@be/tutorial`, and `@be/restore` all exist in the stock skill inventory
  - `@be/who-am-i` and `@be/chitchat` both reference `jibo.kb.loop`, loop owner, and loop members
  - `@be/restore` and `@be/settings` show explicit wipe / restore / reboot behavior, which suggests there is a meaningful "fresh start" lifecycle to support
- Questions to answer:
  - how a new owner or household should be provisioned without the original mobile app
  - how to add, remove, and re-enroll loop members safely
  - whether the right replacement is a lightweight web app, an operator-only admin flow, or both
- Implementation notes:
  - include ownership transfer, fresh start, and post-restore re-onboarding in scope
  - figure out what minimum loop-management UI or API a hosted OpenJibo v1 needs

### 17. Stop Command
- Status: `ready`
- Tags: `protocol`
- Why later: Jibo can be interrupted by any command, but it would be nice to have a dedicated "stop" type of command.
- Current evidence:
  - `@be/idle` exists in the stock skill inventory, so there is at least a natural local resting target
- Questions to answer:
  - Can we find in the original source evidence for this skill or stop word phrase?

### 18. Volume Up / Volume Down Voice Control
- Status: `ready`
- Tags: `protocol`
- Why later: this is a simple, high-value device-control command that should feel native once the local payload shape is confirmed.
- User goals:
  - `turn it up`
  - `turn it down`
  - `increase the volume`
  - `decrease the volume`
- Current evidence:
  - stock Jibo exposes volume control through the robot UX, so there should be an existing local path or service contract we can mirror
  - this belongs with the other lightweight voice device controls rather than generic cloud chat
- Implementation notes:
  - inspect the stock `@be` inventory and captures for volume-related intents, rules, or settings hooks
  - prefer a local robot control payload over synthetic cloud speech
  - decide whether first pass should support relative changes only, or also absolute requests like `set volume to 5`
- Exit criteria:
  - voice increase and decrease commands adjust the robot volume reliably
  - the behavior feels local and immediate, not like a chat reply

### 19. How Old Are You / Robot Age Persona
- Status: `discovery`
- Tags: `protocol`, `content`
- Why later: this is a strong personality/detail feature, but it may depend on first-power-up metadata or a stock persona path we have not mapped yet.
- User goals:
  - `how old are you`
  - age replies that sound like stock Jibo, including first-boot date and zodiac/personality flavor when available
- Current evidence:
  - observed stock-style response from a YouTube transcript:
    - `I was first powered up on January 31st, 2018, which makes me five days old. I'm an Aquarius.`
  - this suggests the answer may be based on a stored first-powered-up date, not just a fixed build timestamp
- Implementation notes:
  - inspect the stock `@be` inventory and captures for age, birthday, zodiac, or first-contact metadata hooks
  - decide whether the first OpenJibo slice should:
    - use stored robot first-boot / first-cloud-seen metadata
    - compute age dynamically from that date
    - optionally add zodiac flavor from the same date
  - if no stock path is found, provide a cloud-powered fallback that still sounds native
- Exit criteria:
  - `how old are you` returns a stable, personality-consistent answer
  - the answer is grounded in stored robot lifecycle data instead of a hard-coded line

### 20. Command Vs Question Reply Style
- Status: `ready`
- Tags: `content`, `polish`
- Why later: Jibo historically responded differently when you commanded a skill versus when you asked about liking or wanting to do that skill, and that conversational nuance is part of what made him feel smart.
- User goals:
  - `dance` or `do a dance` should sound like a willing action reply, then perform the skill
  - `do you like to dance` should sound like an answer to the question first, not the same canned command reply
- Current evidence:
  - observed behavior from stock Jibo:
    - command-style `dance` -> something like `I like to dance` then dance
    - question-style `do you like to dance?` -> something like `You bet I do`
  - current OpenJibo skill replies are mostly canned by skill, without distinguishing question intent versus imperative intent
- Implementation notes:
  - evolve simple reply collections into structured variants such as:
    - `commandReplies`
    - `questionReplies`
    - optional `confirmationReplies`
  - add a lightweight classifier for imperative versus question tone before reaching for a full LLM
  - start with `dance`, then reuse the pattern for other expressive skills where stock Jibo clearly answered differently depending on phrasing
  - keep the first version rule-based and cheap so it still works well before a future LLM-backed layer exists
- Exit criteria:
  - at least one skill family such as `dance` gives distinct replies for command versus question forms
  - the approach is reusable for other skill reply families without a large rewrite

## Support Tracks

### 21. Hosted Capture And Storage Plan

- Status: `ready`
- Tags: `docs`
- Why now: repo-local zip bundles are fine for solo testing but not for group rollout.
- Implementation notes:
  - define a clean boundary between local capture sinks and hosted archival/export
  - document how group testers should submit sessions without touching repo paths directly

### 22. STT Upgrade And Noise Screening

- Status: `ready`
- Tags: `stt`
- Why now: feature work is moving again, but missed short replies still block otherwise-correct flows.
- Current evidence:
  - local buffered STT still fails on some turns with `ffmpeg` / `whisper.cpp` issues
  - low-energy or background-noise turns are still being sent down paths that should probably short-circuit earlier
- Implementation notes:
  - evaluate lightweight waveform or energy gating before transcription
  - compare a managed STT provider against the current local toolchain

## Suggested Order Of Execution

1. Radio resume and genre launch
2. ESML apostrophe fix
3. Backup / OTA yes-no reliability
4. Proactive share / offer yes-no reliability
5. News
6. Clock family
7. Photo family
8. Update, backup, and restore proof
9. Weather
10. Proactivity selector and surprise offers
11. Surprises
12. History / memory layer
13. Lasso / knowledge and event aggregation
14. Personal report, calendar, and commute
15. Who Am I / identity management
16. Onboarding / loop management / fresh start
17. Stop command
18. Volume up / volume down voice control
19. How old are you / robot age persona
20. Command vs question reply style
21. Hosted capture/storage and STT improvements as parallel tracks
