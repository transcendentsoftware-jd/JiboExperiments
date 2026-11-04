# Explicit integration-transfer runtime preview

[`runtime-preview-41768412801`](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/tag/runtime-preview-41768412801)
implements private integration setup after a robot changes portal accounts.
The new owner configures calendar feeds and Home Assistant again. Pairing to the
same account preserves settings. A → B → A does not revive A's original proof,
feed or Home Assistant link.

Source: `cb33f0c46aacfa062e3fb351196f6e3be2641d8a`. Both images are Linux/amd64 with local
Whisper `base.en` and audio-focused FFmpeg. This is an opt-in prerelease; existing
image pins and default/recommended selectors are unchanged.

| Profile | Runtime image digest | Starter ZIP SHA-256 |
| --- | --- | --- |
| portable | `sha256:e624e4b665c671270ce9ce1f7368799c61c6111e2156fd9049fa9845c6a81d5a` | `376fc6636319a7819125edb44ba4e0bc3813d483ad127f2ae9e44743d0feb857` |
| avx2 | `sha256:0751de785ba77dd15725ba76ec5cee6c83624b1946c03eab6c212f64205abe92` | `6124c5876c6f3fb4230ddea4c7a3129c16ede5d63bfb8fb87d255959006f9e50` |

## Prepare and launch

Use the current trusted checkout, Python 3.9+ and a GitHub CLI supporting starter
attestation verification. From `OpenJibo`, prepare a fresh portable folder:

```text
python3 -I -B scripts/cloud/prepare-published-starter.py --channel preview --version runtime-preview-41768412801 --cpu-profile portable --destination /private/openjibo-transfer-preview
```

Use the appropriate local Python command on Windows, such as `py -3`. Preparation
verifies the exact archive hash and source-bound signature before extraction;
it does not initialize private keys or start Docker. Keep the archive,
attestation and `SHA256SUMS` together when supplying local archive/attestation
files. Signature verification still requires its trusted CLI prerequisites.

Check the new installation before starting it:

```text
python3 -I -B scripts/cloud/launch-published-starter.py --directory /private/openjibo-transfer-preview --project jibo-transfer-preview --port 8099
```

Add `--start` to initialize its private environment and start that project after
the check passes. For AVX2, select `--cpu-profile avx2` during preparation and use
`--avx2-local-daemon` during launch on the same native Linux x86_64 daemon host.
CPU flags from a different client machine do not qualify the daemon host.

## Account transfer and recovery

Sign in again once after upgrade. Version 3 portal tokens bind the current
ownership generation. Keep the private signing key for subsequent restart and
restore continuity. Without a configured signing key or private status-password
fallback, each API process uses a random key and login is required after restart.

Integration snapshot schema 3 retains inactive encrypted records for recovery;
it does not erase shared household data. Older schema 1/2 files can be read
without rewriting them, and the next explicit mutation saves schema 3. An older
runtime supporting only schema 2 refuses the new file. Roll back with the complete
matching pre-upgrade backup and keys, with every API writer stopped. See
[integration ownership](integration-ownership.md) and the
[recovery guide](self-hosted-recovery.md), including the corrected legacy path
`/app/api/App_Data/user-integrations.json` for standard images.

Home Assistant needs its saved opaque link ID to reconnect automatically. An
instance name alone receives a fresh pairing code. Transfers revoke inactive
local connections; other instances recheck ownership on use. The encrypted file
store remains single-writer, and already dispatched external work is not cancelled.

## Recorded acceptance

Source PR and merged-source CI each passed all five jobs and 2,780 backend tests
with zero skips. Independent verification checked both images, SBOMs and signed
starters; the ZIPs were reconstructed byte-for-byte from reviewed source.

Native Ubuntu acceptance passed 36 portable and 40 AVX2 parent checks. Each profile
also passed 20 actual HTTP/WebSocket ownership scenarios and six retained-state
checks after restart and after separate-volume restore. The audit compared
original device IDs, canonical robot IDs, creation times, loop associations,
primary selection, ownership/legacy generations and all stable encrypted fields
with the backup. Three private record generations remained encrypted while only
the current generation appeared through the API. Only expected Home Assistant
last-seen metadata advanced after restored reconnect.

Twenty recorded synthetic local-Whisper turns passed with Azure Speech disabled,
including restart/restore and overlapping AVX2 clients. Verification codes came
from synthetic typed hub turns. These are fixture observations, not a performance
SLA, physical microphone test or robot playback proof. Both actual signed
preparations passed on Windows; new-image Windows runtime, macOS/ARM/emulation
and physical OOBE/repeat deployment acceptance remain separate gates.

The initial AVX2 restore hit the host's exhausted Docker default address pools.
Its failure and logs were retained. The case resumed from the same verified
backup, environment and keys on a new restore-only subnet checked against host
routes and Docker networks. Two extra checks cover that range comparison and
backup re-verification. No older network was pruned or daemon configuration
changed. See [Docker network-pool recovery](self-hosted-docker-network-pools.md).
This was a successful resumed restore, not a first-attempt native run.

All new services are stopped with private keys, volumes and backups retained.
All 18 older running containers remained running without restart. No physical
Jibo or production cloud was changed. The release's
[acceptance summary](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/runtime-preview-41768412801/acceptance-summary.json)
is a supplemental record, not an additional signed attestation subject. These
results do not certify all hosted tenant isolation, general version-pair upgrade
compatibility or stable/license readiness.
