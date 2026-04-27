# Regression Test Plan

## Purpose

This plan is the repeatable live regression checklist for OpenJibo Cloud releases.

Use [live-jibo-test-runbook.md](live-jibo-test-runbook.md) for the environment setup and capture mechanics. Use this file for what to test once the robot is connected and the hosted `.NET` cloud is running.

The goal is to reduce trial-and-error cycles: every live pass should prove the release theme, keep prior working paths warm, and produce enough evidence to separate payload bugs, local robot behavior, and STT quality issues.

## When To Run

Run this plan:

- after the last code change before calling a release complete
- after any fix that touches websocket turn finalization, local skill redirects, constrained yes/no, or STT
- before moving from `1.0.18` bug-fix closeout into `1.0.19` feature work

For small feature slices, run the automated `.NET` tests plus the smoke checks and only the live sections that share the same machinery. Before release closeout, run the full current-release suite.

## Required Evidence

For each live pass, keep these artifacts together under a named test folder such as `artifact-output/jibo-test-N`:

- `.NET` console logs
- websocket captures and fixture exports
- HTTP captures when startup, update, backup, media, or upload paths are involved
- robot runtime logs pulled after the session
- operator notes with exact phrases attempted and visible robot/menu state

Record failures with the observed transcript, active listen rules, emitted websocket response shape, and whether the robot menu state agreed with the cloud response.

## Release Gates

A release is not ready until these are true or explicitly deferred in [development-plan.md](development-plan.md):

- focused `.NET` cloud tests pass
- running robot reports the expected cloud version by voice and `/health`
- no current-release path emits obsolete OpenJibo-only websocket events such as synthetic pending/context/ack packets
- known working live paths still work: startup, simple chat, radio, basic news, constrained yes/no, alarm, and gallery/create
- any remaining failure is classified as cloud payload, local robot state, STT/audio quality, environment/routing, or deferred feature gap

## Automated Baseline

Run before the live session:

```powershell
dotnet test tests\Jibo.Cloud.Tests\Jibo.Cloud.Tests.csproj --no-restore --nologo -v minimal
```

Expected result for the current baseline: all tests pass.

## Live Smoke Checks

Run these first so obvious environment problems do not pollute feature results:

1. Start the `.NET` cloud using the live runbook.
2. Confirm `/health` reports the expected version.
3. Ask `cloud version`; confirm Jibo speaks the same version.
4. Run one simple chat turn.
5. Run one joke turn.
6. Confirm websocket capture is being written before continuing.

Stop and fix environment issues if startup, websocket connection, or capture output is not clean.

## Current `1.0.18` Regression Suite

### Radio

Goal: keep the local radio redirect path proven.

- Say `open the radio`.
- Say `play country music`.
- Expected: Jibo opens or resumes the radio locally, and the country phrase carries a `Country` station entity.
- Capture check: websocket output should be local `SKILL_REDIRECT` plus silent completion, not generic chat speech.

### News

Goal: keep the Nimbus-shaped cloud skill path proven.

- Say `tell me the news`.
- Expected: Jibo plays the current synthetic quick brief.
- Capture check: `LISTEN` match includes `cloudSkill = news`, followed by a `news` `SKILL_ACTION`.
- Current limitation: provider-backed and category-expanded headlines are deferred unless selected as the optional feature slice.

### Backup, OTA, And Share Yes/No

Goal: prove constrained yes/no prompts stay local and do not leak global launch rules.

- Trigger the update menu path when available and answer one short `yes` or `no` prompt.
- Exercise any available share/date/offer yes-no prompt and answer both `yes` and `no` across runs when practical.
- Observe backup-in-progress behavior separately from explicit voice commands.
- Do not treat a spoken `take a backup` failure as proof of the backup scheduler path; that command is not currently wired as a hosted-cloud voice feature.
- Expected: short `yes`/`no` replies map locally, empty replies no-input locally, and backup/download notifications are not repeatedly re-announced once acknowledged.
- Capture check: active rule remains the constrained rule such as `surprises-ota/want_to_download_now`, `settings/download_now_later`, `shared/yes_no`, or another stock prompt rule.

### Alarm

Goal: prove the clock skill behaves locally and menu state agrees after the `jibo test 24` fixes.

Start from a known state. If an alarm already exists, record it and clear it through the menu or a controlled voice delete before beginning.

Test these paths:

- explicit set: `set an alarm for 7:43 AM`, adjusted to a near-future time during the actual run
- compact set: `set alarm for 743`, adjusted to a near-future time during the actual run
- clarification: `set an alarm`, then answer the value prompt with a short time such as `7 44` or `7, 44`
- replacement: with an alarm already set, set a different alarm and answer the replacement prompt; verify whether the answer kept or replaced the old alarm
- value-prompt cancel: `set an alarm`, then say `cancel`
- voice delete: `delete my alarm` or `cancel alarm`
- no-input cleanup: allow one value prompt to miss or time out when practical

Expected:

- successful set paths appear in the robot alarm menu and fire at the expected time
- replacement prompt answer changes or preserves the alarm consistently with the robot's question
- `cancel` inside the value prompt closes without scheduling
- voice delete clears the robot menu state
- empty value prompt turns complete locally instead of generic `I heard you` speech

Capture check:

- clock payloads use local `@be/clock` handoff with alarm entities when a value exists
- missing values stay in local clock clarification
- `CLIENT_NLU cancel` under `clock/alarm_set_value` or `clock/timer_set_value` maps to local clock `cancel`
- no-input under `clock/alarm_set_value` or `clock/timer_set_value` returns local `LISTEN`/`EOS` only

### Photo Gallery And Create

Goal: prove gallery/create no longer leaves stale listening state after yes/no or preview prompts.

Test these paths:

- `open photo gallery`
- if gallery is empty, answer `yes` to the offer to take a picture
- take one photo and answer the keeper prompt with `yes`
- repeat a gallery empty prompt or create keeper prompt with a missed/empty answer when practical
- if using disposable test photos, test delete confirmation once with `no` and once with `yes`

Expected:

- empty gallery `yes` redirects to `@be/create`
- empty gallery `no` exits cleanly when tested
- keeper `yes` completes and Jibo settles without a stale blue ring
- empty `shared/yes_no`, `create/is_it_a_keeper`, and `gallery/gallery_preview` turns no-input locally instead of generic `I heard you`
- delete confirmation only deletes on a positive `yes`

Capture check:

- gallery launch redirects to `@be/gallery`
- create photo redirects to `@be/create/createOnePhoto`
- local no-input replies keep the active constrained rule and strip unrelated global launch rules

### STT And Audio Quality

Goal: avoid misclassifying transcript failures as payload regressions.

For every failed voice turn, record:

- phrase attempted
- transcript observed in websocket capture
- active listen rule
- whether the transcript was empty, collapsed, or semantically wrong
- whether local `ffmpeg` or `whisper.cpp` logged an error

Expected:

- no `ffmpeg` failure should become the dominant failure mode for non-Opus buffered audio
- short replies such as `yes`, `no`, `cancel`, and short alarm times should either map correctly or be classified as STT misses with evidence

## Optional Feature Slice Checks

When a new feature is added before a release closes:

- add two or three exact phrases to this section before live testing
- capture one successful path and one near-miss phrase if the feature is voice-routed
- keep the test narrow enough that a failure can be fixed or deferred without reopening the whole release

For the current candidate list, add cases here when implemented:

- stop command: `stop`, `stop that`, `never mind`
- volume: `turn it up`, `turn it down`, `increase the volume`, `decrease the volume`
- robot age/persona: `how old are you`

## After The Run

After each session:

1. Summarize pass/fail by section.
2. Mark each failure as cloud payload, local robot state, STT/audio, environment, or deferred gap.
3. Import any high-value websocket fixture.
4. Update [development-plan.md](development-plan.md) with latest live evidence.
5. Update [feature-backlog.md](feature-backlog.md) with what remains in the current release versus what moves to the next release.
