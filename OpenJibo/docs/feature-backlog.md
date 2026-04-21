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
- Exit criteria:
  - `tell me the news` reaches a non-placeholder live path
  - robot behavior feels Nimbus-native rather than generic chat playback

### 6. Clock Family Audit

- Status: `ready`
- Tags: `protocol`
- Why now: clock, date, timer, and alarm menu hooks are already visible in captures and the robot repo has a real `@be/clock` skill.
- Current evidence:
  - [protocol-inventory.md](C:/Projects/JiboExperiments/OpenJibo/docs/protocol-inventory.md) already tracks menu intents for `askForTime`, `askForDate`, `timerValue`, and `alarmValue`
  - `@be/clock` exists in the robot skill inventory
- Implementation notes:
  - compare our custom time/date path against actual menu payloads
  - decide whether timer and alarm should stay robot-local with cloud acknowledgement, or whether cloud needs to shape the launch and follow-up turns
- Exit criteria:
  - time/date behavior stays correct
  - timer and alarm launch or set correctly from both menu and voice where applicable

### 7. Photo Family Audit

- Status: `ready`
- Tags: `protocol`, `docs`
- Why now: photo confirmation improved already, and the robot skill inventory includes `gallery`.
- Current evidence:
  - `@be/gallery` exists in the robot skill inventory
  - current captures already show `snapshot` and related menu destinations
- Implementation notes:
  - separate three flows:
    - snap a picture
    - photo gallery
    - photobooth
  - document whether each one is local-only, cloud-assisted, or upload-backed
- Exit criteria:
  - known photo menu and voice phrases map to the correct local path
  - capture storage expectations are documented for laptop versus hosted testing

## Discovery Queue

### 8. Weather As Cloud Report Plus Local Presentation

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

### 9. Surprises Routing

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

### 10. Personal Report, Calendar, And Commute

- Status: `discovery`
- Tags: `protocol`, `content`
- Why later: these are already stubbed in `.NET`, but the robot-side ownership still needs clearer mapping.
- Current evidence:
  - current `.NET` placeholders live in [InMemoryJiboExperienceContentRepository.cs](C:/Projects/JiboExperiments/OpenJibo/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Content/InMemoryJiboExperienceContentRepository.cs)
  - Nimbus has personal-report hooks, but the exact cloud contract still needs confirmation
- Questions to answer:
  - should calendar and commute be independent feature paths or sections inside personal report
  - what minimum provider data shape lets Jibo present these naturally

## Support Tracks

### 11. Hosted Capture And Storage Plan

- Status: `ready`
- Tags: `docs`
- Why now: repo-local zip bundles are fine for solo testing but not for group rollout.
- Implementation notes:
  - define a clean boundary between local capture sinks and hosted archival/export
  - document how group testers should submit sessions without touching repo paths directly

### 12. STT Upgrade And Noise Screening

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
8. Weather
9. Surprises
10. Personal report, calendar, and commute
11. Hosted capture/storage and STT improvements as parallel tracks
