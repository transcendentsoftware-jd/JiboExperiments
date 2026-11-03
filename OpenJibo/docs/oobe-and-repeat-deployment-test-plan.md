# OOBE and repeat deployment laboratory plan

Recorded November 3, 2026: the owner purchased two reseller-described new/OOBE
Jibos. Arrival dates, actual firmware, provisioning history and physical state are
unknown. These planned resources are not completed tests or admitted profiles.

| Role | Current status | Initial purpose |
| --- | --- | --- |
| Existing robot | Available; established user configuration | Preserve as regression control for current voice, games and normal operation |
| OOBE-A | Arrival pending; reported new/OOBE, unverified | Intake baseline and first deployment after recovery prerequisites |
| OOBE-B | Arrival pending; reported new/OOBE, unverified | Preserve an independent stock/OOBE reference until A's first path is understood |

Use private lab labels for serial/identity associations. Public reports contain
only case IDs, release/source pins, redacted outcomes and evidence hashes. Do not
publish credential files, keys, Wi-Fi details, raw identity or personal captures.

## Intake before provisioning

For each incoming unit, record arrival, reported/observed condition, initial
screen/OOBE behavior and whatever release information is accessible without
changing state. Do not assume the two devices have identical images or untouched
provisioning merely because their seller descriptions match. Retain the first
baseline before network enrollment, conversion, updater use or reset testing.

Acquire and verify device-specific calibration/storage recovery evidence through
an admitted read-only path before any proposed device mutation. Associate hashes,
sizes, partition layout and release evidence privately with the correct unit.
Do not use A's calibration, credentials or recovery image as B's baseline. Unknown
layouts/features stop that profile's admission; do not guess GPT labels or mounts.

## Ordered acceptance cases

| Case | Platform/device | Evidence required |
| --- | --- | --- |
| HOST-01 | Fresh isolated server | Source/image/config pins, explicit Linux engine/CPU, secrets initialized once, database migration/health and selected endpoint observations |
| HOST-02 | Same server, repeat startup | Same encryption keys, data volumes and selected image; migration ledger and existing ownership/state survive; no duplicate records |
| HOST-03 | Separate restore rehearsal | Database and matching encryption material restored into a new isolated project; verify state and keep the original intact |
| OOBE-01 | A initial baseline | Observed installed release, mode, partition/filesystem mapping and device-specific recovery checks; no invented credential region |
| OOBE-02 | A first provisioning | Owner/loop/robot association, issued credentials/tokens, selected API/Hub/DNS/TLS route, normal-mode transition and speech; exact logs/readback hashes |
| OOBE-03 | A interrupted/retried provisioning | Bounded failure/retry behavior; no duplicate ownership/loop/robot, partial secrets or false completion; retain the incomplete attempt |
| REPEAT-01 | A same-target deployment repeat | Identity/ownership and calibration preserved, no unintended key rotation or duplicate enrollment; already-matching configuration treated explicitly |
| REGRESSION-01 | Existing robot | Current voice/socket/game behavior compared with its original baseline; track Word of the Day final blue-ring/no-input sound separately |
| OOBE-04 | B independent first deployment | Repeat the admitted first-deployment procedure from B's own baseline; verify the result without importing A's identity/recovery material |
| MULTI-01 | A/B/existing across selected instances | Issued-token client isolation, concurrent sockets, ownership boundaries and credential separation; tokenless one-client mode cannot qualify this case |
| RECOVERY-01 | A later dedicated recovery/reset window | Verified rollback/restore procedure and retained baseline first; explicitly scoped reset/write/readback evidence rather than assuming a deployment repeat means wipe |

Start with A and preserve B as a reference. Keep the existing robot's functioning
configuration as the control. Do not run a reset just to make a repeat-start test
look like OOBE. Server restart, same-target reconfiguration, version update,
provider switch, factory reset and recovery are different cases.

## Current implementation gaps

The offline AutoMod v2 review requires a credential file containing a matching
region. Fresh OOBE may have missing/empty credentials. That case needs a separate
typed intake/provisioning contract; do not relax the current validator, manufacture
credentials or default the region to `api`. Actual firmware/native routing must
determine any region/host policy. The source-documented paths are not a substitute
for the arriving robot's retained mount/release evidence.

The readiness packet observes Docker prerequisites and runtime configuration and
exports a selected isolated target. `/health/self-hosted` does not query the
database or certify migrations, persistence, image provenance or robot protocols.
The existing tokenless compatibility guard admits one client address only. Three
robots require issued-token acceptance or deliberately separate isolated servers.

## Run records and promotion

For every attempt retain a unique case/attempt ID, robot role, source/image and
configuration pins, input/output hashes, start/finish times, expected/observed
state, failure point and recovery disposition. Preserve failed attempts. A retry
does not erase a failure; a pass on A does not qualify B or a different firmware.
Each result stays pending until its specific evidence exists.

No physical execution window is opened by this document or by purchasing the
devices. Arrival is not a scheduled task or permission to alter the current robot.
CI uses disposable synthetic state; its volume-destroying teardown is not a real
installation upgrade/rollback command. Never apply that teardown to the lab's
retained data. Only promote support claims for the exact host, runtime and robot
release combinations actually exercised with recovery evidence.
