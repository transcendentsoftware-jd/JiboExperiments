# Signed starter preview candidate, 2026-10-06

This is a new preview candidate, not a stable release or an in-place update.
The previously tested `runtime-preview-36863178102` release remains unchanged.
Neither runtime deployment nor physical-robot testing is implied.

Published source: `fd32678bb29ade975c71a9bcd7dc7a89babaf541`.
[Publisher run 37540893708](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/37540893708)
passed both profiles, including actual ZIP signature verification.

| Profile | Runtime digest | Starter ZIP SHA-256 |
| --- | --- | --- |
| portable | `sha256:6bb69fd68d2c4863feaf17fd90319125ed1534f8cf728a4070ce04ee116c6010` | `5e38ef4e26920db0909f407ef7b53ac49ecd8d63f33dc9f631ca32f10ac66210` |
| avx2 | `sha256:5311f91890754171b9f87bac288e9179b4ba6512a527c3c66569d2525e228fa6` | `30e2e9b9555042698b2dc25e32fdd7b0da282d8e08fdaf4de53ad394c5dec6c2` |

Image namespace: `ghcr.io/transcendent-software-llc/openjibo-runtime`.
Both ZIPs were also verified locally through GitHub using the trusted publisher
workflow, exact source commit, GitHub Actions issuer and hosted-runner policy.
An actual check using the previous release's source commit rejected the new ZIP.
No checksum-only fallback or extraction occurred for that rejected check.

[Independent image-and-ZIP verification run 37541750517](https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/37541750517)
passed before exposing this candidate as a downloadable prerelease.
It checked both registry pulls, image labels and image attestations, then downloaded
the successful publisher evidence and independently verified both ZIP signatures.

The prerelease tag is `runtime-preview-37540893708`, targeting the
published source above. Retain the original ZIP bytes and their distinct
`starter-portable-attestation.json` / `starter-avx2-attestation.json` signature
bundles. Never replace the previous release assets or set a latest/stable tag.

[The public prerelease](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/tag/runtime-preview-37540893708)
now retains all four assets. Both ZIPs were downloaded back from this release
and passed signature verification using their published local attestation bundles.
The returned image/profile identities and SHA-256 values matched the table.

After publication, download and inspect a trusted checkout's verifier, then
verify the portable ZIP before extracting:

```bash
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/verify-starter-provenance.py \
  --bundle starter-portable-preview.zip \
  --expected-sha256 5e38ef4e26920db0909f407ef7b53ac49ecd8d63f33dc9f631ca32f10ac66210 \
  --source-commit fd32678bb29ade975c71a9bcd7dc7a89babaf541 \
  --attestation starter-portable-attestation.json
```

GitHub CLI with attestation verification support is required. A local signature
bundle does not guarantee offline trust-root verification. Trusted pins come
from this reviewed record, not an arbitrary checksum next to a download.

The targeted preparer and guarded launcher now support both reviewed previews.
The new version requires signed provenance automatically; missing or invalid
signatures cannot select checksum-only preparation. The launcher recognizes
each exact manifest/image pair and reports the selected version. Legacy commands
remain supported; there is no moving latest selection or upgrade path.

Actual HTTPS download/preparation passed with `provenance_verified: true` and
`docker_started: false`. The resulting files passed the launcher's exact
manifest/payload checks. Native Linux launch/socket/audio acceptance subsequently
passed as recorded below. Exact-candidate restart persistence and backup/restore,
Windows,
SBOM/license approval, channel freshness/revocation and physical-robot playback
remain outstanding. The AVX2 profile additionally requires compatible CPU
features on the actual Docker daemon host.

## Separate native Linux candidate test

Prerequisites include GitHub CLI with `gh attestation verify` support, Python 3,
Bash, OpenSSL and Docker Compose 2.24.4+. Keep all existing test stacks intact.
Use a new destination, Compose project and loopback port; stop on any error.
Do not rerun fresh preparation/start against an existing destination. If GitHub
CLI is absent or too old, stop and resolve that prerequisite rather than bypassing
signature verification.

First inspect the selected identity without downloading or creating files:

```bash
(
set -euo pipefail
cd ~/JiboExperiments
git pull --ff-only
command -v gh
gh attestation verify --help >/dev/null

python3 -B OpenJibo/scripts/cloud/prepare-published-starter.py \
  --channel preview --version runtime-preview-37540893708 \
  --destination ~/Downloads/openjibo-signed-preview-test --plan
)
```

Prepare the new portable ZIP; provenance is mandatory for this version even
without `--require-provenance`:

```bash
(
set -euo pipefail
gh attestation verify --help >/dev/null
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/prepare-published-starter.py \
  --channel preview --version runtime-preview-37540893708 \
  --destination ~/Downloads/openjibo-signed-preview-test
)
```

Expect `prepared: true`, `provenance_verified: true`, `docker_started: false`.
Inspect the extracted README, then check and explicitly start the new project:

```bash
(
set -euo pipefail
sudo -v
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/launch-published-starter.py \
  --directory ~/Downloads/openjibo-signed-preview-test \
  --project openjibo-signed-preview-test --port 8085 --sudo-docker

sudo -v
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/launch-published-starter.py \
  --directory ~/Downloads/openjibo-signed-preview-test \
  --project openjibo-signed-preview-test --port 8085 --sudo-docker --start
)
```

After `health_passed: true`, run the familiar synthetic acceptance probes:

```bash
(
set -euo pipefail
node ~/JiboExperiments/OpenJibo/src/Jibo.Cloud/node/invoke-jetstream-compatibility-probe.mjs \
  --entrypoint-url http://localhost:8085 \
  --hub-url ws://localhost:8085 --notification-url ws://localhost:8085 \
  --mode authenticated --robots 2 --device-prefix signed-preview --skip-turn

node ~/JiboExperiments/OpenJibo/src/Jibo.Cloud/node/invoke-real-audio-probe.mjs \
  --audio ~/Downloads/cloud-version.ogg --base-url http://localhost:8085 \
  --robot-id speech-acceptance-signed-preview --turns 3
)
```

Share only the JSON results/errors, not `.env` or tokens. This is portable
software acceptance on an isolated loopback stack, not physical-robot testing,
AVX2 fresh installation or production deployment.

If `gh` reports `unknown command "attestation"`, the distro-provided CLI is too
old for these checks. Upgrade using the [official GitHub CLI Ubuntu/Debian repository instructions](https://github.com/cli/cli/blob/trunk/docs/install_linux.md#debian),
then verify the required command before retrying. Each command block above runs
in a fail-fast subshell; failure does not close your interactive terminal.
The preparer also checks CLI capabilities before downloading when signed
verification is required, and reports this prerequisite separately.

## User-reported native Linux acceptance, 2026-10-09

The user upgraded GitHub CLI to 2.102.0. Ubuntu ESM's older 2.45.0 package had
APT priority 510 versus 500 for the official GitHub repository, so a normal
upgrade kept the older CLI; explicit version selection resolved that prerequisite.
Authentication and access to the ZIP attestation succeeded. A previous target
directory existed and was preserved; preparation succeeded into the fresh
`/home/jake-dubin/Downloads/openjibo-signed-preview-20261009T124133Z` directory.
Its report had `provenance_verified: true`, the expected archive/image identity
and `docker_started: false`.

The read-only launcher check passed for `openjibo-signed-preview-test`, port 8085.
Explicit startup then returned `health_passed: true` and the expected signed
preview version/image. Two consecutive socket/audio probe batches passed:

| Probe batch | Two-robot socket checks | Audio turn durations (ms) |
| --- | --- | --- |
| 1 | 612 ms | 9576, 7135, 7001 |
| 2 | 350 ms | 5235, 6949, 6767 |

Both `signed-preview-1` and `signed-preview-2` connected to notification, listen
and proactive sockets in each batch. Socket tests used `--skip-turn`, so this
does not prove proactive transaction replies. All six audio transactions matched
`cloud version`, validated the cloud instruction and returned `LISTEN`, `EOS`,
`SKILL_ACTION`. Timing alone does not establish the provider or a cold-start cause.
This evidence is user-reported terminal output, not agent execution on the laptop.

The signed portable preparation-to-launch-to-synthetic-speech path has passed.
Restart persistence and separate backup/restore for this exact candidate remain
open; older release recovery evidence is not silently transferred to it.
Representative encrypted-data recovery and physical microphone/playback are also
not proven. Preserve the private `.env`, data keys and all existing volumes.

### Next: API restart without reinitialization

This restarts only the signed candidate API, using its existing Docker
configuration rather than recomputing Compose variables. It does not rerun the
fresh launcher, migrator or initializer. After health recovers, the probe verifies
reconnection; it is not proof that database rows survived, because a probe can
recreate records. Database persistence must be checked separately with API stopped.

```bash
(
set -euo pipefail
cd "$HOME/JiboExperiments"
sudo -v
api=openjibo-signed-preview-test-api-1
expected=sha256:6bb69fd68d2c4863feaf17fd90319125ed1534f8cf728a4070ce04ee116c6010
test "$(sudo -n docker inspect --format '{{.Config.Image}}' "$api")" = \
  "ghcr.io/transcendent-software-llc/openjibo-runtime@$expected"
sudo -n docker restart "$api"
curl --fail-with-body --max-time 15 --retry 10 --retry-delay 2 \
  --retry-connrefused --retry-all-errors http://localhost:8085/health
node OpenJibo/src/Jibo.Cloud/node/invoke-jetstream-compatibility-probe.mjs \
  --entrypoint-url http://localhost:8085 \
  --hub-url ws://localhost:8085 --notification-url ws://localhost:8085 \
  --mode authenticated --robots 2 --device-prefix signed-preview --skip-turn
)
```
