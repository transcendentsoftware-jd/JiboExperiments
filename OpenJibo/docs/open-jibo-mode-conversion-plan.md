# Open Jibo Mode Conversion Plan

## Purpose

This plan turns the current device bootstrap path into a repeatable Open Jibo conversion path.

The goal is to let a stock Jibo opt into Open Jibo, choose the right server mode, preserve owner data, and return to stock behavior when needed. The first version can still be operator-assisted, but it should be designed like the beginning of an owner-safe product path.

## Release Target

For `1.0.20`, the target is a proven planning and prototype path:

- document the conversion model and rollback invariants
- identify the robot files and skill hooks that must be modified
- build non-destructive audit helpers before any write helpers
- make the audit/plan stages predictive so they can fail closed before any conversion write if the baseline, target mode, required skill set, or rollback snapshot is missing
- define the Open Jibo onboarding/config skill contract
- prove one controlled conversion on the known `1.9.2` baseline before expanding the matrix

Full public owner-ready conversion can land later if the test matrix or hardware helper work needs more time.

## Mode Model

Open Jibo should use explicit modes instead of overwriting stock configuration in place.

| Mode | Purpose | Expected server target |
| --- | --- | --- |
| `normal`, `oobe` or `int-developer` | Original Jibo behavior and rollback target (reference as `stock`) | original region/config where available |
| `open-jibo` | Default managed Open Jibo experience | managed Open Jibo production cloud (`api.openjibo.com`, `open-jibo-socket.openjibo.com`, `neohub.openjibo.com`) |
| `open-jibo-ai` | Open Jibo with higher-level AI/orchestration features | `openjibo.ai` or managed AI-capable cloud |
| `open-jibo-self-hosted` | Owner-managed local or private server | owner supplied host/region |
| `open-jibo-developer` | Testing/debugging mode for development builds and captures | developer supplied host/region |

Decision: `open-jibo-ai` should be a distinct mode/server track, not just a feature flag under `open-jibo`.

The AI mode is expected to run a different suite inside the Open Jibo server network. It should use modern AI as the brain, with agent orchestration and dispatch, while still being able to control Open Jibo Cloud as a tool so the new brain can preserve old-style Jibo behavior when appropriate.

Decision: `open-jibo-self-hosted` must support fully isolated operation. A future hybrid mode can sync with the main Open Jibo network, but only after a trust relationship and admission model are defined.

Decision: hybrid sync should begin as a one-way setup choice, not as a casual toggle. If a self-hosted robot/server opts into sync with the main Open Jibo network, going back to fully isolated mode should require a reset/OOBE-style recovery and another RCM conversion pass. That keeps support and security simpler while the trust model is immature.

## Conversion Invariants

These rules should hold for every implementation:

- never require a one-way conversion
- snapshot touched files before modifying them
- record the currently active stock mode/region before switching away
- preserve robot identity, owner identity, loop identity, and certificates unless a deliberate repair step is selected
- preserve owner content such as holidays, Jibo birthdate, photos/videos, person voice/face training, and favorites/lists
- make conversion idempotent so rerunning the setup does not corrupt config
- make rollback possible from both script and robot menu skill
- prefer adding a new region/mode entry over replacing the stock entry
- keep destructive repair actions behind explicit operator confirmation
- do not blindly trust identity values found on the robot, because cloned or previously modified devices may share identifiers

## First-Boot Conversion Behavior

After an RCM-assisted conversion package is applied, the robot should boot into the Open Jibo onboarding/config skill.

If the user completes onboarding:

- Open Jibo remains enabled
- the selected mode becomes active
- the Open Jibo skill remains available in the menu for configuration and future rollback

If the user breaks out of onboarding:

- the robot returns to the previous stock behavior as closely as possible
- the Open Jibo skill remains available in the menu so conversion can be resumed later
- the conversion state records that first-boot onboarding was abandoned

If the RCM helper is used again:

- the helper can reset the conversion state
- the next boot can launch the Open Jibo onboarding/config skill again
- this gives robots stuck on error screens another chance to recover through Open Jibo setup

For version 2 or other robots that can already boot normally, the Open Jibo menu option can exist without forcing immediate conversion.

Rollback should be supported as long as Open Jibo changes remain compatible with the stock configuration. The plan should assume that full rollback may become harder later as Open Jibo starts modifying deeper robot behavior.

## Robot Identity Trust Model

Open Jibo should treat stock robot identity as evidence, not as an unquestioned primary key.

Reason:

- other revival and repair efforts may have cloned or modified robot files
- multiple robots may present the same historical robot name, ID, token, certificate, or account data
- some identity values may be useful for recovery but unsafe as unique identifiers in the new cloud

Proposed model:

- preserve the observed stock identity values as legacy identity claims
- issue a new Open Jibo robot identity during conversion or first cloud registration
- bind legacy claims to the new Open Jibo identity only after validation
- persist the new Open Jibo robot ID/token back to the robot where possible
- detect future devices that present the same legacy identity but lack the Open Jibo issued identity
- treat duplicate legacy identity presentation as a clone/repair scenario that needs a fresh Open Jibo identity
- keep an audit trail linking legacy identity claims, issued Open Jibo identity, conversion date, account/loop, and device fingerprints where available

Example:

| Observed legacy claim | Open Jibo mapping |
| --- | --- |
| robot name: `cherry-pie-robot` | legacy display/name claim |
| legacy ID/token: `abc1234567` | untrusted legacy identity claim |
| issued Open Jibo robot ID: `cpr-12345-xyz` | trusted Open Jibo identity |
| issued Open Jibo token: `xyz9876543` | persisted Open Jibo credential |

If another robot later presents `cherry-pie-robot` plus `abc1234567` without the issued Open Jibo identity, the cloud should not merge it into the existing Open Jibo robot record automatically. It should create or request a new identity flow.

Current identity hypothesis:

- `Notification.NewRobotToken` includes a `deviceId` and credential signing information
- the original cloud likely used `deviceId` to locate an issued token or credential record, then verified request signing against that record
- the OOBE/mobile-app flow likely generated or retrieved a token from the cloud, embedded that token with Wi-Fi information in the QR code, and let the robot use it to complete onboarding
- `CreateRobot`, `CreateHubToken`, `CreateAccessToken`, or a neighboring registration flow may issue or exchange the token that later participates in signing
- issuing a new Open Jibo token during conversion may give us the cleanest trust root, with all robot-presented stock values treated as mapping evidence after the fact

Implemented runtime boundary (`2026-09-21`): a credential-valid `CreateHubToken` request can bind the resulting
hashed durable Hub token to a one-way access-key fingerprint and positive account credential epoch. PostgreSQL
revalidates that binding, token expiry, and revocation on every new WebSocket handshake, including on another
replica. Compatibility-shaped or unsigned requests still receive unbound tokens, so this does not change stock
robot behavior. The binding proves only continuity with an exportable account credential; it does not prove the
request body, physical robot identity, ownership, or co-presence and cannot automatically merge or assign a
robot. The current slice provides the persistence and enforcement seam; an operator or future credential-rotation
workflow must advance the epoch whenever credential material changes without changing the access-key identifier.

Research targets:

- capture and compare `Notification.NewRobotToken` requests across known-good and suspicious devices
- locate where `deviceId`, onboarding token, robot token, hub token, and signing credentials are persisted on the robot
- determine whether `deviceId` is hardware-derived, first-boot-derived, or file-derived
- determine whether a cloned file system naturally reuses `deviceId` or regenerates it
- find which token or key material is used for signing each request family

Open question: decide which device fingerprints are stable and safe enough to use as supporting signals without making owner recovery impossible.

## Known Configuration Inputs

Current strongest evidence:

- `/usr/local/etc/jibo-jetstream-service.json`
- `/var/jibo/credentials.json`

Additional files to audit:

- `/usr/local/etc/jibo-ssm/*.json`
- `/usr/local/bin/jibo-ssm/node_modules/@jibo/jibo-server-client/lib/region_config.json`
- `/usr/local/bin/jibo-ssm/lib/skills-service-manager.js`
- `/usr/local/lib/libJiboServerService.so`
- `/skills/jibo/Jibo/Skills/@be/be/node_modules/language-subtag-registry/data/json/registry.json`
- `/skills/jibo/Jibo/Skills/oobe-config/config.json`
- local skill manifest/config locations for menu visibility and first-boot behavior
- update, backup, restore, media, and person-recognition state locations
- token and signing credential locations used by `Notification.NewRobotToken`, account, hub, and robot registration flows

Region-template replacements the conversion scripts now target in every discovered `region_config.json` and `aws-sdk-all.js` copy:

- `{service}.{region}.api.jibo.com` -> `{service}.{region}.api.openjibo.com`
- `https://api.jibo.com` -> `https://api.openjibo.com`
- `http://api.jibo.com:8080` -> `http://api.openjibo.com:8080`
- `https://{region}.jibo.com` -> `https://{region}.openjibo.com`
- `http://{region}.jibo.com:8080` -> `http://{region}.openjibo.com:8080`
- `wss://{region}-socket.jibo.com` -> `wss://{region}-socket.openjibo.com`
- `ws://{region}-socket.jibo.com:8090` -> `ws://{region}-socket.openjibo.com:8090`

Live `jibo-ssm` runtime bundle replacements the conversion scripts now also target:

- `data.region + ".jibo.com"` -> `"api.openjibo.com"`
- `this._wifiService.options.region + ".jibo.com"` -> `"api.openjibo.com"`
- previously converted `.openjibo.com` concatenations and compact no-space forms -> `"api.openjibo.com"`
- `API: 'api.jibo.com'` -> `API: 'api.openjibo.com'`

The SSM value is an HTTPS `GET /` connectivity probe, not the Jetstream region endpoint. Keep `credentials.region` set to `open-jibo` for region selection, but make this probe use the canonical `api.openjibo.com` host rather than deriving `open-jibo.openjibo.com`.

Native `jibo-server-service` compatibility patch:

- v1 stock/patched MD5: `ae82f1dd7407f8d74b287917cb9a8b24` -> `e55e18e92aa6365569f13214e0118745`
- v2/lastdance stock/patched MD5: `a863a238d6f2531446d0eb0d1d358c19` -> `688ec2940ed1fc7d1b86d2fd29bc6b30`
- early-model stock/patched MD5: `e3f3e394815bebb37427f65ead579e0f` -> `db55f99018e2348c2754b1daa6e1df39`
- replace exactly two equal-length ASCII occurrences of `jibo.com` with `jibo.pro`
- `NotificationSubsystem::getSignedHeaders()` then signs `https://open-jibo.jibo.pro`
- `NotificationSubsystem::getRobotToken()` then connects to `open-jibo.jibo.pro:443`
- no decompilation, recompilation, ELF resizing, or ARM instruction changes are required
- the cloud binds `open-jibo.jibo.pro` and `open-jibo-socket.jibo.pro` directly; do not use HTTP redirects for signed POST or WebSocket traffic

The script coverage should include every discovered copy under:

- `/usr/lib/node_modules/@jibo/jibo-server-client/...`
- `/usr/lib/node/@jibo/jibo-server-client/...`
- `/usr/lib/node_modules/@jibo/jibo-ota-updater/node_modules/@jibo/jibo-server-client/...`
- `/usr/lib/node/@jibo/jibo-ota-updater/node_modules/@jibo/jibo-server-client/...`
- `/usr/lib/node_modules/@jibo/jibo-log-client/node_modules/@jibo/jibo-server-client/...`
- `/usr/lib/node/@jibo/jibo-log-client/node_modules/@jibo/jibo-server-client/...`
- `/usr/local/bin/jibo-ssm/node_modules/@jibo/jibo-server-client/...`
- `/opt/jibo/Jibo/Skills/@be/be/node_modules/@jibo/jibo-server-client/...`
- `/opt/jibo/Jibo/Skills/oobe-config/node_modules/@jibo/jibo-server-client/...`

## Onboarding And Config Skill

The Open Jibo skill should be the owner-facing control surface for conversion.

Required first behaviors:

- show whether Open Jibo is enabled
- show the current mode
- enable `open-jibo`
- switch to another Open Jibo mode when supported
- disable Open Jibo and return to the remembered stock mode
- stay visible in the menu after disable so the owner can re-enable it
- run a first-boot/OOBE-style setup after conversion
- explain when a reboot is required
- abandon onboarding cleanly and restore the prior behavior when the user backs out
- reset first-boot setup state when the RCM helper reapplies the conversion package
- warn the user when identity repair or clone handling is needed
- explain what was repaired when a new Open Jibo robot identity is issued

## Confirmed App Contract

The app research and the new `Open_Jibo_APP` tree now give us a concrete onboarding shape to align against:

- app-side flow:
  - `ScreenWelcome`
  - `ScreenTip`
  - `ScreenAuth`
  - `ScreenWifi`
  - `ScreenQR`
  - `ScreenSetup`
  - `ScreenSuccess`
- app-side server calls:
  - `Account_20151111.Create`
  - `Account_20151111.Login`
  - `Loop_20160324.List`
  - `Loop_20160324.ListMembers`
  - `Loop_20160324.InviteMember`
  - `Loop_20160324.UpdateMember`
  - `Media_20160725.List`
  - `OOBE_20161026.PrepareRobot`
  - `OOBE_20161026.GetStatus`
- QR construction:
  - SSID, password, optional static IP block, then access token
  - XOR obfuscation with the classic Jibo key phrase
  - chunked QR display when the encoded payload is too large
- fallback behavior:
  - if `EXPO_PUBLIC_OPENJIBO_SERVER_URL` is missing or unreachable, the app falls back to the static token `JiboLivesSo`

Implication: the first conversion proof should not invent a brand-new setup shape. It should align the robot skill and cloud endpoints to this app sequence, then use the original protocol docs to fill in the remaining robot-side handoff details.

Likely future behaviors:

- pair the robot to the Open Jibo account surface
- select hosted, self-hosted, hybrid, or developer server
- scan or enter a self-hosted server code
- run a compatibility check
- export a diagnostic bundle for support
- show whether backup/update/restore are healthy

Decision: first-boot setup starts automatically after RCM-assisted conversion. If the user exits onboarding, the robot returns to its previous behavior and keeps the Open Jibo skill in the menu.

Decision: when the skill detects a possible cloned or modified identity, it should tell the user what was found and either prompt before repair or explain the repair that was already required to continue. The tone should be calm and recovery-oriented: the user should understand that Open Jibo is protecting their robot/account identity, not accusing them of doing something wrong.

## Scripted Conversion Package

Build scripts in layers:

1. Audit
   - detect software version and distribution
   - list current region/mode/config values
   - list candidate files that would be touched
   - report whether required skills and OS features appear present
   - record the rollback snapshot candidate and reject the plan if a safe snapshot cannot be produced
   - use `scripts/bootstrap/audit-openjibo-conversion.sh` as the first non-destructive helper
2. Plan
   - generate a proposed patch manifest
   - show backup paths and rollback plan
   - validate selected mode and target server values
   - prove the plan is internally consistent before any write step is allowed to run
   - use `scripts/bootstrap/plan-openjibo-conversion.sh` to turn the audit into a proposed manifest
3. Apply
   - snapshot files
   - add Open Jibo region/mode entries
   - stage the Open Jibo conversion marker and first-boot pending state
   - rewrite the live credentials region to `open-jibo` during apply
   - install or update the Open Jibo skill
   - prepare rollback metadata for a clean restore path
4. Verify
   - confirm config files parse
   - confirm robot startup reaches the chosen cloud
   - confirm the Open Jibo skill appears in the menu
   - confirm rollback metadata exists
5. Rollback
   - restore previous stock mode/region
   - leave the Open Jibo skill available when possible
   - preserve snapshots and diagnostics

## Device Compatibility Matrix

Start with the known-good baseline and expand outward.

| Device / distribution | Goal | Status |
| --- | --- | --- |
| Stock `1.9.2` baseline | first controlled conversion and rollback proof | target first |
| New/OOBE stock robot | prove first-boot setup path | priority target |
| Pre-`1.9.2` stock robot | identify missing skills or update prerequisites | discovery |
| Version 2 / last release variants | identify feature rollback or compatibility gaps | priority target |
| NTT variant | identify region/config differences | discovery |
| MIT-special variant | identify custom distribution differences | discovery |

Decision: version 2 and new/OOBE robots are equally important early targets. NTT and MIT-special variants are later targets because they are rarer and not publicly available, but group-member access can support validation when the core path is stable.

## Hardware Easy Button Track

The Jibo Revival Group hardware path should be planned beside the scripts, not after them.

Decision: the helper should be a guided owner-facing recovery appliance. Internally it can transport and run scripts, but the owner experience should be a controlled guided flow that starts on hardware and continues through the robot skill plus website/app onboarding.

### OOBE OTA Bootstrap Discovery (`2026-06-30`)

Maaarcna reported a working prototype that can drive a wiped/OOBE Jibo into OTA without SSH by using the setup QR's static networking fields:

- the OOBE QR payload is plaintext lines in this order: `ssid`, `password`, `staticIP`, `netmask`, `gateway`, `dns1`, `dns2`, and trailing access token
- when static networking is present, `oobe-config` writes the supplied DNS values into the robot network configuration
- a controlled DNS server can resolve `api.jibo.com`, NTP pool names, and related stock hosts to a LAN bootstrap server
- the bootstrap server can present the original `*.jibo.com` GoDaddy certificate and pair it with a small NTP service that answers with a 2017-era clock, letting the robot's old trust store accept the otherwise expired certificate
- once OOBE reaches the stock update path, the server can answer `GetUpdateFrom(subsystem, fromVersion, filter)` requests and serve per-subsystem OTA tarballs
- a Jibo release is a manifest plus subsystem tarballs; known apply targets are `os` to the inactive rootfs slot, `services` to `/usr/local`, and `skill` to `/opt/jibo/Jibo/Skills/<name>`
- each OTA asset still needs correct SHA-1 and Content-Length metadata, and the robot may reboot several times while applying updates

Impact on the conversion plan:

- this becomes a high-priority parallel bootstrap lane, not a replacement for the ShofEL/firewall/SSH lane yet
- for wiped or intentionally reset OOBE robots, OTA bootstrap may become the lowest-friction owner path because it can start from app plus QR and avoid opening SSH before the robot trusts Open Jibo
- for non-wiped robots, broken OOBE state, older baselines, or variants where DNS is rewritten after first setup, ShofEL plus firewall/SSH remains the recovery and rollback lane
- the first OTA package must preserve or install the future Open Jibo trust/region configuration before stock OTA cleanup erases temporary SSH or DNS-only changes
- the plan should support a local bootstrap appliance profile that can provide QR generation, DNS override, time spoofing, HTTPS with the historical certificate chain, OTA metadata, and OTA package hosting
- the public hosted cloud should not depend on shipping the historical private key; if the group uses that certificate for lab bootstrap, the owner-facing path needs a safer packaging and disclosure model before release

Immediate questions and blockers:

Use `scripts/bootstrap/plan-oobe-ota-bootstrap.sh` to produce a machine-readable plan for this lane before touching a physical robot. The helper keeps the path planning-only, records DNS/NTP/HTTPS/OTA assumptions, and fails closed in strict mode while certificate provenance or trace evidence is missing.

1. Confirm the provenance, redistribution rights, and security handling of the historical `*.jibo.com` certificate and key before placing any related material in this repository or in an owner-facing tool.
2. Capture Maaarcna's prototype request/response traces for `PrepareRobot`, `SetupRobot`, `GetStatus`, `GetUpdateFrom`, asset downloads, NTP sync, and post-update verification.
3. Validate whether OOBE can be launched from a normal boot without wiping owner data, and whether that path still honors QR-provided DNS long enough to run OTA.
4. Decide how the first Open Jibo OTA package survives the reboot/apply sequence and retargets the robot to `api.openjibo.com` or the selected self-hosted server without relying on the expired stock certificate after conversion.
5. Build package provenance rules: source packages from known stock OTA/Nexus archives where legal, avoid robot-unique files, and produce reproducible Open Jibo subsystem tarballs with manifest hashes.
6. Confirm multi-robot behavior for a LAN bootstrap server, including access token handling, per-robot identity issuance, simultaneous OOBE sessions, and rollback if one robot fails mid-update.

Planned helper-device sequence:

1. Owner connects the helper device to the robot's RCM USB path.
2. Owner puts the robot into RCM with the required button sequence.
3. The helper runs the exploit/recovery transport needed to gain the initial patch window.
4. The helper applies the smallest version-aware firewall or trust patch needed to make SSH reachable over the USB/LAN path.
5. The robot reboots out of RCM and becomes reachable over SSH.
6. The helper snapshots the robot before any conversion writes.
7. The helper installs or updates the Open Jibo skill and supporting conversion assets over SSH.
8. The helper writes the minimum conversion state:
   - region / jetstream routing
   - mode selection
   - first-boot / onboarding pending state
   - any version-specific bookkeeping we later confirm is required
9. The robot reboots into the Open Jibo mode.
10. The Open Jibo skill completes onboarding and finalizes the conversion.
11. If the owner exits onboarding, the skill remains in the menu and the robot falls back to the prior state as cleanly as possible.
12. If any step fails before completion, the helper restores the snapshot and returns the robot to the original state.

Expected recovery flow:

1. Owner connects the helper to the robot's RCM USB path.
2. Owner uses the required button combination to enter RCM.
3. Helper runs the group ShofEL-based access path.
4. Helper applies the smallest safe patch needed to open the firewall path, ideally by locating the target file with binary/hex signatures and relative offsets instead of doing a full raw backup first.
5. Robot reboots into a state where SSH is reachable over the USB/LAN path proven by the group frankencable work.
6. Helper performs a fast full backup over the USB/LAN network path.
7. Helper installs the Open Jibo skill and conversion package over SSH.
8. Helper runs the conversion scripts remotely and records rollback metadata.
9. Robot reboots into the Open Jibo onboarding/config skill.
10. Owner continues guided setup between the robot skill and the website/app.

Safety requirements:

- do not skip backup once SSH/LAN access is available
- make the initial firewall-opening patch as small and version-aware as possible
- verify target file signatures before patching bytes
- produce a recovery bundle before applying Open Jibo changes
- keep rollback possible from the helper even if the robot skill fails to launch
- do not apply a conversion write unless audit and plan have both confirmed the target baseline and a usable rollback snapshot
- show clear owner-visible state for waiting, backup, patching, rebooting, and failure

## QA And Exit Criteria

For `1.0.20`, this track is ready to build when:

- the modified file list is known for the `1.9.2` baseline
- the skill install/menu strategy is known
- the app-side onboarding contract above is mapped to the robot handoff and backend endpoints
- rollback behavior is specified
- first-boot behavior is specified
- the audit helper can run without changing the robot
- the compatibility matrix has an owner/device for the first two test rows

This track is ready to close for `1.0.20` when:

- a `1.9.2` robot can be audited, converted, booted into Open Jibo, confirmed by cloud version, and rolled back
- the onboarding/config skill appears in the menu and can report the active mode
- owner data preservation checks pass for the known local data categories
- all unknowns that cannot be resolved in `1.0.20` are split into follow-up backlog items

## Open Questions

1. Which device fingerprints are stable and safe enough to support identity validation?
2. Where should the issued Open Jibo robot identity/token be persisted on each robot version?
3. What should the onboarding skill show when it detects a possible cloned or previously modified identity?
4. What exact token exchange should Open Jibo emulate or replace during OOBE and `NewRobotToken` registration?
5. Should `open-jibo-hybrid` become a distinct mode label, even if sync remains a one-way setup choice?
