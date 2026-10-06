# Published runtime acceptance — 2026-10-06

Evidence is user-reported Ubuntu laptop terminal output, not tests executed by
the development agent on that host. This tests the actual public portable bundle
and registry images, rather than substituting locally built image IDs.

## Passed

- Portable digest `sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b`
  pulled and started in separate `openjibo-published-preview` on loopback 8082.
  Health returned version 1.0.20; two synthetic identities authenticated and
  connected to notification, listen and proactive sockets in 597 ms.
- Three acoustic cloud-version transactions passed with LISTEN, EOS and
  SKILL_ACTION; phrase and instruction checks were true. Times: 6801, 7232,
  7369 ms. This does not prove physical playback or STT provider selection.
- API restart recovered health; both identities' socket checks passed in 583 ms.
- With API stopped, both identity rows existed before and after PostgreSQL
  restart. No intervening probe could recreate these records.
- With API quiesced, both databases and API data volume were backed up, with
  the existing environment retained privately. Original API was restarted.
- Separate fresh `openjibo-published-restore` restored both databases and data
  volume. SQL found both identities before API startup. Portable migrator
  exited 0, API health passed on loopback 8083, and PostgreSQL was healthy.
- Restored socket checks passed in 572 ms; three acoustic turns passed in
  8383, 7568 and 7296 ms.
- Local CPU preflight passed across eight reported logical processors. Published
  AVX2 digest `sha256:b39887ad412d4eee77bb20ca15fe5ae82bae2024d72487b7947ca37e8426bdf0`
  pulled and replaced only the restored API. Image identity and health matched.
  Three acoustic turns passed in 2738, 1096 and 1060 ms. Warm samples are
  consistent with earlier AVX2 improvements; three turns are not a capacity test.

## Remaining gates

AVX2 ZIP fresh installation was not tested (the AVX2 image was applied to an
existing restored project). Windows published-bundle installation, physical
microphone/playback, representative encrypted-data decryption after restore,
broader audio/noise coverage, sustained load, signed download metadata and
SBOM/license certification remain outstanding. Socket connection checks used
`--skip-turn` and do not certify proactive transaction responses. This is not
stable, upgrade or hybrid/managed acceptance. Keep backup credentials private.

## Targeted preparation foundation

`scripts/cloud/prepare-published-starter.py` requires explicit preview channel,
this exact version and a new destination. It downloads from bounded approved
HTTPS endpoints or accepts a local ZIP, checks the independently pinned archive
hash, reuses the complete archive verifier and checks image/profile identity
before extraction. It starts no Docker process, creates no secrets and performs
no migrations. A plan-only mode performs no download or filesystem writes.

Development checks passed six offline safety tests and actual preparation of the
published portable bytes via both a local ZIP and an anonymous HTTPS download.
Those preparation checks ran on Windows; native-Linux preparer permissions are
covered by CI assertions, with user execution still pending. The existing bundle
verifier remains responsible for the exact file inventory, traversal/link
rejection and internal manifest checks. No Docker stack was started by these
development checks.

From a trusted checkout (Python 3.9+), inspect both this preparer and the adjacent
`verify-openjibo-starter-bundle.py`, then preview:

```bash
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/prepare-published-starter.py \
  --channel preview --version runtime-preview-36863178102 \
  --destination ~/Downloads/openjibo-preparer-test --plan
```

Prepare only when that destination does not exist:

```bash
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/prepare-published-starter.py \
  --channel preview --version runtime-preview-36863178102 \
  --destination ~/Downloads/openjibo-preparer-test
```

Do not launch this directory until selecting another unused Compose project and
loopback port. Existing preview/restore stacks must stay untouched. This is a
preparer foundation, not the completed installer or a signed release selector.

The user subsequently ran the preparer on native Ubuntu successfully. It
reported the expected archive hash, portable digest and `docker_started: false`
for a new `~/Downloads/openjibo-preparer-test` directory. This closes native
preparation execution, not automatic launch acceptance.

## Explicit launch foundation (native Linux portable preview)

`launch-published-starter.py` defaults to read-only checking. It verifies the
exact extracted manifest and payload hashes, refuses an existing `.env` or extra
files, checks native Linux/amd64 prerequisites, Docker access, Compose 2.24.4+,
RAM/free disk screening, existing project resources and loopback-port conflicts.
Unix endpoint evidence is best-effort locality, not daemon host identity proof.
The disk check covers the prepared directory filesystem, not a remote/separate
Docker data filesystem; these checks are not speech/capacity certification.

Explicit `--start` rechecks prerequisites, records a fresh-only launch attempt,
uses the verified environment initializer with a randomly generated PostgreSQL
password, writes a loopback-only override and starts the pinned API/migrator
without rebuilding. It waits for health. Secrets are not printed or passed on
the command line. Partial failures preserve files, keys and volumes; this command
does not resume failed attempts or silently upgrade existing installations.

Run Python as the normal user, not root. If Docker needs sudo, authorize it in
the console first and pass `--sudo-docker`; only Docker uses `sudo -n`. These
launch commands use a new project and port, distinct from existing 8080–8083
stacks:

```bash
cd ~/JiboExperiments
git pull --ff-only
sudo -v

python3 -B OpenJibo/scripts/cloud/launch-published-starter.py \
  --directory ~/Downloads/openjibo-preparer-test \
  --project openjibo-launch-test --port 8084 --sudo-docker
```

Only after that check passes, explicitly launch:

```bash
sudo -v
python3 -B ~/JiboExperiments/OpenJibo/scripts/cloud/launch-published-starter.py \
  --directory ~/Downloads/openjibo-preparer-test \
  --project openjibo-launch-test --port 8084 --sudo-docker --start
```

Do not rerun preparation or fresh launch on this directory afterward. Inspect
the exact named project on failure, keeping `.env` private. Health alone is not
socket/audio acceptance. The manual published-image test already passed, but
end-to-end automatic launch by this script remains pending user execution.
