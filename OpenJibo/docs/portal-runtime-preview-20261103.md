# Portal ownership and session-purpose preview, 2026-11-03

The [explicit experimental release runtime-preview-41757874916](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/tag/runtime-preview-41757874916)
includes the
[portal session boundaries](portal-session-boundaries.md): account, robot and
administrator purposes are signed separately; robot requests recheck current
ownership; and unconfigured instances no longer share a development signing
key. Upgrade requires **one fresh portal login**. Robot/account data and
user-data encryption keys are unchanged.

The runtime was built from `2a0cc6fdf718ffcb60399770ce9d4118e54720f8` by
[publisher 41757874916](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/41757874916).
Both profiles use Linux/amd64, local Whisper `base.en` and the audio-focused
FFmpeg variant. This is a new opt-in identity; older releases and default or
recommended selectors are unchanged.

| Profile | Image digest | Starter ZIP SHA-256 |
| --- | --- | --- |
| Portable | `sha256:7a6b86f60204c18e97fe870c68f26cb94c52a9c143738b840b937d0fc5956758` | `a23c0b1f19619e29dc8e669a0ff3dbd9591fb2e85e466163c64cbac646ca5816` |
| AVX2 | `sha256:c33cc7c29ff23366fd1cb75d34f86145783246a4911509207f143225198dfc8a` | `9baf78173db0d1c98adc255a932454aedf1eac10bc03a8be9e8bf493c55c3e65` |

Manifest SHA-256: portable
`145774383d9c56e543de4f1d1642f2d7e1bc7a62515f8c348b65e7b2d5a5c2f1`;
AVX2 `4f4983b9c840e292478ab95b895a6899b02a86e373ab51ba0e2373e3abdc95e8`.

## Explicit preparation and launch

With the current reviewed preparer and its provenance prerequisites, from
`OpenJibo`, use a new private destination with an existing parent:

```text
python3 -B scripts/cloud/prepare-published-starter.py --channel preview --version runtime-preview-41757874916 --cpu-profile portable --destination /private/portal-preview
```

Use `py -3` on Windows. `--plan` describes the pinned selection without
downloading or writing. Actual preparation always requires source-bound signed
provenance and never creates secrets or starts Docker. AVX2 is an explicit
`--cpu-profile avx2` selection with a different signed ZIP.

The native Linux x86_64 launcher checks the fresh installation separately:

```text
python3 -B scripts/cloud/launch-published-starter.py --directory /private/portal-preview --project openjibo-portal-preview --port 8097
```

Only add `--start` after reviewing the check and release guide. AVX2 additionally
requires `--avx2-local-daemon`, the same native system Docker socket and all
required CPU features. It is not admitted through WSL, emulation or a remote
daemon. Windows uses the bundled PowerShell entrypoint. These are fresh-install
tools, not an automatic updater.

Configure a retained private `OPENJIBO_PORTAL_SESSION_SIGNING_KEY` when portal
sessions must survive later restarts or work across replicas. An explicitly
configured signing key takes priority over the private status-password fallback.
With neither configured, a random process-local key is used and login is
required after restart. Fresh preparation does not invent a shared portal key.
Keep existing encryption keys and all other settings when adding this optional
configuration; do not reinitialize an existing environment.

Use `--profile self-hosted` with the original project and files for manual
stop/down operations; an inactive profile can leave containers running. Retain
all volumes. Follow the [backup/recovery guide](self-hosted-recovery.md) before
replacement, including exporting and staging any old container-local encrypted
integration file. The [bounded older-preview upgrade rehearsal](preview-upgrade-recovery-20261103.md)
tested a different target digest and does not independently certify upgrades to
this new one.

## Evidence and limits

Source PR and merged-source CI each passed all five jobs, 2,770 backend tests
with zero skips and three-OS prerequisite contracts. Fourteen pre-fix regression
failures were retained, followed by 84 passing focused portal checks and two
additional claim/transfer proofs, including two application instances using real
PostgreSQL. A committed transfer rejected both old account-selected and
spoken-code proofs on the other instance while allowing the new owner.

Both publisher jobs passed. Downloaded starter ZIPs were independently
reconstructed byte-for-byte from reviewed source and passed actual source-bound
signature verification. Exact image/profile/audio labels, model and retained
source hashes matched. [Independent registry verification 41758459798](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/41758459798)
passed both image/SPDX and starter attestations at the exact digests above.

Both profiles passed fresh signed preparation and native launch on a separate
Ubuntu 24.04.5 x86_64 host (i5-8250U/8GB, Docker 29.8.2, Compose 5.6.0):

| Profile | Parent checks passed | Recorded local speech turns | Observed times |
| --- | ---: | ---: | --- |
| Portable | 36 | 7 | 5,802–7,048 ms |
| AVX2 | 38 | 13 | Single client: 511–1,018 ms; overlapping clients: 568–1,031 ms |

Each profile also passed 13 delegated real HTTP/WebSocket portal scenarios:
typed synthetic “verify me” turns issued actual codes, account pairing and
transfer used normal endpoints, both old ownership proofs were denied on five
robot routes, the new owner retained access, and unauthorized integration
deletion left the new owner's link intact. A robot named `portal-admin` could
open its own dashboard but received 401 on administrator routes. Configured
administrator login passed. These typed turns do not prove microphone or
physical spoken-code behavior.

After real API/PostgreSQL container replacement and separate-volume backup
restore, five retained-state checks per stage confirmed that old proofs stayed
denied, the new owner's signed session and explicit administrator purpose still
worked, and the original encrypted integration command secret was retained.
These continuity checks used an explicitly configured private signing key.
Three baseline robot identities/creation times/loop links and primary selection,
personal memory and a binary volume fixture also survived recovery.

A read-only audit compared archived identities and the transferred owner/link
timestamp with the restored database. The exact image's shipped persistence
code compared the archived, source and restored integration snapshots without
rewriting them. Every stable field and the original command secret matched.
The restored Home Assistant reconnect legitimately advanced `LastSeenUtc` and
re-encrypted the file. An initial supplemental audit's byte-identity assumption
was corrected to this semantic check; its original source was retained.

All 20 recorded local-Whisper turns returned the expected cloud-version
transcript/instruction, including after restart/restore and two overlapping AVX2
clients, with Azure Speech disabled. Timings are bounded fixture observations,
not an SLA. Actual signed CLI preparation also passed for both ZIPs on Windows,
without creating secrets or starting Docker; new-image Windows runtime
acceptance was not performed.

All newly owned services stopped with private environments, keys, volumes and
backups retained. All 18 older running containers remained running without
restart. Only redacted reports were transferred from the host. The release's
`acceptance-summary.json` records these results. All 18 public assets were
downloaded back and checked against staged bytes/API digests, and both downloaded
starter signatures were reverified. Supplemental reports and source evidence
are not additional signed attestation subjects.

This is bounded portal HTTP validation, not complete hosted tenant isolation.
It does not cancel in-flight requests, certify protocol/socket authorization,
revoke external integration sockets or make logout globally durable. Physical
OOBE/repeat deployment and playback, macOS/ARM/emulation, all-version upgrades
and stable license/notice readiness remain separate gates. Never transfer
older-digest runtime results silently to this one.
