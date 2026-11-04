# Self-hosted setup and recovery preview, 2026-11-03

[Experimental release runtime-preview-41729015780](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/tag/runtime-preview-41729015780)
uses source `ac0b30b11025944cb0e88d4a3437c39f07307a8d`. This is an explicit
preview selection. Older versions, default/recommended selectors and deployments
are unchanged. Both images are Linux/amd64 with local Whisper `base.en` and the
audio-focused FFmpeg variant. AVX2 requires a compatible native daemon host;
portable does not establish a certified old-hardware matrix.

| Profile | Image digest | Starter ZIP SHA-256 |
| --- | --- | --- |
| portable | `sha256:be8d1d08825de31f910c7dab78d2f3c779a5b2f8735f6bed83298173a8d12169` | `66febd2c39e0e4ec9c923c086eaf6cfee064020f3a662033d27c8119ae972709` |
| avx2 | `sha256:964fc94249f91f9d78dbf6dba2e8bb8c7740de1019772a70e1f01acdec9cf24f` | `b8a0ece11ecdd393af0c7522dcf10e5daf9a4e9a4d4fb9dfd3922ec5bd78be7f` |

The corresponding manifest SHA-256 values are
`3ba01263fd60b185e42451c114d642697a4311af2e44656adaa87852ea936d8d`
(portable) and
`6886a6fc9d561ff3b11f000024ffe8f470a4a4617f402a1dc52987f6d1a3ffb5`
(AVX2). The launcher recognizes these exact bytes; it does not admit arbitrary
new manifests or mutable image tags.

## Prepare without starting anything

With Python 3.9+ and a GitHub CLI supporting the existing provenance checks,
from `OpenJibo` run:

```text
python3 -B scripts/cloud/prepare-published-starter.py --channel preview --version runtime-preview-41729015780 --cpu-profile portable --destination /private/new-jibo-preview
```

Use `py -3` on Windows and replace the destination with a new private directory
whose parent exists. `--plan` prints the pinned selection without downloading
or writing. The actual preparation always requires the source-bound signature;
it never initializes secrets, starts Docker or upgrades an existing directory.
AVX2 requires explicit `--cpu-profile avx2` and has a separate immutable ZIP.

For native Linux x86_64, the existing launch check remains separate:

```text
python3 -B scripts/cloud/launch-published-starter.py --directory /private/new-jibo-preview --project openjibo-durable-preview --port 8091
```

Only add `--start` after reviewing the fresh-directory check and release guide.
AVX2 also requires `--avx2-local-daemon`, a local native Linux Docker daemon and
all required CPU flags. The Python launcher does not support Windows, macOS,
WSL, ARM or remote-daemon AVX2 acceptance. Windows has the bundled PowerShell
entrypoint; configure its generated `.env` and follow its README. The
[runbook](self-hosted-runbook.md) and [backup/recovery guide](self-hosted-recovery.md)
cover keys, volume retention and older container-local integration files.

Include `--profile self-hosted` and the original project/files when manually
stopping/removing containers. An unselected Compose profile can leave them
running. CI now requires a different API container ID after its down/up step,
so a no-op teardown cannot count as a real restart.

## Evidence and limits

[Publisher 41729015780](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/41729015780)
and [independent verification 41729967637](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/41729967637)
passed for both profiles. Image/source/CPU/FFmpeg labels, image provenance,
signed SPDX inventories and starter signatures matched. Both ZIPs were
independently reconstructed byte-for-byte from reviewed source. Model and
retained source hashes matched. Source CI passed all five jobs and 2,753 backend
tests without skips.

The portable ZIP passed 28 checks on Windows Docker Desktop using its bundled
PowerShell entrypoint, including fresh migrations, three synthetic robot/loop
identities, primary selection, actual container replacement, bundled backup and
restore into separate volumes, and issued-token sockets. Seven synthetic
recorded speech turns passed local Whisper with Azure Speech disabled, including
after restart and restore, returning expected transcript and cloud-version
instructions in 4,063–4,826 ms. These bounded observations are not an SLA.

Both exact signed packages also passed on a separate native Ubuntu 24.04.5
x86_64 host with an Intel i5-8250U, Docker 29.8.2 and Compose 5.6.0:

| Profile | Passed checks | Recorded speech turns | Observed turn time |
| --- | ---: | ---: | --- |
| Portable | 33 | 7 | 6,088–6,625 ms |
| AVX2 | 35 | 13 | Single client: 520–1,070 ms; overlapping clients: 573–948 ms |

The native runs used source-bound signed preparation, the native launcher and,
for AVX2, its explicit local-daemon/CPU feature gate. Both passed fresh
migrations, issued-token sockets, actual replacement of both API and PostgreSQL
containers, retention of three synthetic device/loop identities and primary
selection, and bundled backup verification. Separate-volume restores retained
synthetic personal memory and a binary API-volume fixture. A read-only database
audit compared restored canonical robot IDs and original creation times with
the archived database. The OOBE response's `robotId` echoes the requested device
alias; it is not the canonical database robot ID.

All 20 native recorded speech turns returned the expected transcript and
cloud-version instruction through local Whisper with Azure Speech disabled,
including after restart and restore. AVX2 additionally passed two simultaneous
speech clients with three turns each. These fixture timings are bounded host
observations, not an SLA. All new test services stopped, with keys, volumes and
private backups retained. All 18 previously running containers remained running
without restart.

The release's supplemental [native acceptance report](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/runtime-preview-41729015780/native-linux-acceptance.json)
and `NATIVE-LINUX-SHA256SUMS` record these results. The original
`acceptance-summary.json` remains the initial Windows-only snapshot; its pending
native flags describe that earlier snapshot. All 18 original assets and their
checksums remain unchanged. Acceptance reports are supplemental records, not
additional signed attestation subjects.

No physical robot, OOBE hardware or playback was tested.
macOS/ARM/emulation, owner isolation, upgrade compatibility and stable
license/notice readiness are not certified. Fresh keys/data were synthetic;
test services stopped with volumes and recovery material retained. Earlier
previews and unpromoted distro-profile run 41728295466 were not substituted for
this audio preview's exact identities.

Local evidence is under
`artifact-output/integration-preservation-evidence-20261103/` and
`artifact-output/native-current-runtime-20261103/`. Only redacted
acceptance metadata and CI build/source evidence are public release assets;
private environments, SQL dumps and local volume archives are excluded.
