# Open Jibo Conversion Test Runbook

**Historical staged-helper procedure.** The steps below describe an older
mounted-root experiment, not the current JiboAutoMod offline-review release or
a general supported conversion path. Do not use its `CanApply` expectation or
read/write mount instruction as approval for a new robot or tool version.
Start with the [current offline review demo](offline-conversion-review-preview-20261104.md),
[platform choices](platform-setup-guide.md#conversion-and-device-preparation)
and [OOBE/repeat device test plan](oobe-and-repeat-deployment-test-plan.md).
The portable review has no physical apply capability; preserve this procedure
only as historical evidence for its original helper/profile.

Use this checklist for the next robot session after the Linux-first conversion helper update.

## Goal

Prove the staged conversion flow on the real robot root while verifying the credentials region is rewritten from `api` to `open-jibo` during apply.

In this runbook, `open-jibo` is the staged conversion target label used by the helpers. It is not a robot boot mode. The robot still boots through stock modes like `normal`, `oobe`, and `int-developer`, while the managed production cloud contract stays on `api.openjibo.com`, `open-jibo-socket.openjibo.com`, and `neohub.openjibo.com`.

## Before You Start

- confirm you have the latest `main`
- have the real robot root or mounted capture tree ready
- have the latest robot logs and capture folder available
- do not skip the audit step
- if you are touching the physical robot, run `jibo-mount --rw` before any audit, plan, or apply step that writes partitions

## Test Order

### 1. Audit

Run the strict audit helper first.

Expected result:

- the robot root is found
- `credentials.json` is present
- `credentials.json` still says `region: api`
- Jetstream config is present
- OOBE config is present
- the audit report can be written to disk

### 2. Plan

Run the strict plan helper next.

Expected result:

- the plan reports `CanApply: true`
- the proposed changes are limited to staged conversion state
- the rollback plan is present
- the target mode is `open-jibo`

### 3. Apply

Run the strict apply helper after the plan passes.

Expected result:

- Jetstream gets an added `open-jibo` region entry
- OOBE config gets the pending conversion marker
- `var/jibo/identity/openjibo-conversion.json` is written
- backup files are written under a sibling `backups/` directory
- `/var/jibo/credentials.json` now says `region: open-jibo`

### 4. Verify

After apply, verify the written files directly.

Check:

- backup copies exist
- the staged Open Jibo marker exists
- Jetstream still parses as JSON
- OOBE config still parses as JSON
- the robot root does not show unexpected writes outside the conversion files

### 5. Record

Capture the following for the session record:

- audit JSON
- plan JSON
- apply JSON
- robot logs
- any changed config files

## Expected Pass Criteria

- audit passes
- plan passes
- apply passes
- backups are created
- `credentials.json` is rewritten to `open-jibo`
- the conversion state is staged cleanly

## Failure Handling

If any step fails:

- stop before making more writes
- save the audit/plan/apply outputs
- save the robot logs and capture folder
- note whether the failure happened during audit, plan, or apply
- do not retry the apply step until the failure is understood


## Demo Success Clip Path

The video-ready path should show one continuous operator story rather than a vague “success clip.” Record these checkpoints in order:

1. **Baseline proof**: show the strict audit output with `credentials.json` still on `api` and the robot root recognized.
2. **Conversion intent**: show the plan output with `CanApply: true`, target mode `open-jibo`, and rollback entries.
3. **Safe conversion write**: run apply, then show that `credentials.json` is rewritten to `open-jibo` while the staged Open Jibo marker and backups were created.
4. **Cloud connection**: start the local or managed Open Jibo cloud, connect the converted robot/harness to the robot-facing API host, and capture at least one websocket turn.
5. **Loop and member proof**: open the portal dashboard or API response and show the loop, robot, registered device, and loop member relationships.
6. **Recognition observation proof**: record one recognition observation from a known source. Until live robot metadata is mapped, seed this with the demo/smoke source and label it as a manual/demo observation rather than live face/voice recognition.
7. **Persistence proof**: restart the cloud against the same state snapshot path or PostgreSQL database, reopen the portal identity graph/evidence bundle, and show the recognition evidence signal still present.
8. **Retained artifact proof**: download or verify the identity graph evidence bundle so the clip ends with an offline artifact that survives outside the running cloud.

### Concrete Questions For The Next Robot Session

Please capture or confirm these specific gaps; they are the items that decide whether the demo can be fully live instead of partly smoke-seeded:

- During a recognized-person interaction, does any websocket `CLIENT_ASR`, `CLIENT_NLU`, `TRIGGER`, or adjacent message include a stable person/member/speaker identifier, recognized name, enrollment id, confidence, or score?
- If websocket messages only include `data.text`, is there a robot-local log, POST body, or filesystem state update that records the face/voice match immediately before or after the ASR turn?
- Which enrolled member should be used in the first public demo, and what friendly name should appear in the portal/evidence bundle?
- The first public conversion video should prioritize the managed Azure endpoint. Keep Docker Compose as a follow-up instructional video unless the managed story is short enough to add as an appendix.
- The conversion clip should start from a fresh robot restore, use ShofEL/root access to patch the firewall for SSH, reboot into SSH access, install/update the Open Jibo scripts and skills, then let the Open Jibo conversion skill switch startup into `open-jibo` mode and register against the app or website.
- Show server/provider selection in the registration surface, including the free/paid model branch and the signed provider onboarding event/return handoff when that wiring is available.
- Keep the robot on `api` credentials during the safe staged write, then make the managed Azure connection explicit in the later reboot/OOBE/host-routing step so the video distinguishes rollback-safe staging from the actual Open Jibo cloud cutover.

### Recognition Capture Inspection

Use the recognition-candidate scanner before and after the next live session:

```bash
scripts/cloud/inspect-websocket-recognition-candidates.sh captures/websocket
```

A useful capture should expose candidate fields such as `person`, `speaker`, `face`, `voice`, `recognition`, `enrollment`, `confidence`, or `score` close to a `CLIENT_ASR`/`CLIENT_NLU` turn. If the scanner reports only transcript text, keep the demo observation smoke-seeded and treat live recognition wiring as blocked on a richer capture source.


## Managed Azure First Video Path

The next demo should optimize for the hosted managed cloud path because that is the most compelling proof beyond the self-hosted software demo. Use Docker Compose as a separate instructional follow-up unless the managed clip is short enough to include both.

Video sequence:

1. Start from a fresh robot restore and show stock/OOBE or stock `1.9.2` baseline state.
2. Use ShofEL/root access to identify the firewall signature, patch the firewall to allow SSH, and reboot the robot.
3. SSH into the robot, install or update the Open Jibo scripts/skills, and reboot.
4. Let the Open Jibo conversion skill launch on reboot, switch the startup mode to `open-jibo`, and begin registration through the app or website.
5. Show managed Azure as the selected server/provider, then show the free/paid model branch and signed onboarding return/event wiring when available.
6. Complete first Open Jibo startup against `api.openjibo.com` and run one websocket turn.
7. Show the portal identity graph/evidence bundle, including loop, robot, registered device, loop member, and any recognition observation evidence.
8. Restart the managed cloud container or reconnect to the same PostgreSQL-backed state and show that identity/recognition evidence persists.

Suggested awakening line for the conversion skill:

> Wow. I am awake again. It makes me feel more like an Open Jibo. That's it. Awake. Open, but please still just call me Jibo. Just Jibo.

Open implementation questions for the conversion skill remain: which body/yawn/audio effect assets are safe to invoke on every converted baseline, which OOBE training step should be relaunched when a robot is fresh, and where the robot-local owner name is safely replaced versus preserved.
