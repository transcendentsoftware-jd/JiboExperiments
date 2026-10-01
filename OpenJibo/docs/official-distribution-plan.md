# Official site, builds and hosting starters

Status: delivery plan with digest launchers and organization-owned public runtime
previews, 2026-10-01. Existing source-build and Azure
deployment tools are foundations, not a published official download service.
This plan does not authorize publishing artifacts, changing DNS, spending on
new hosts, enrolling servers or modifying robots.

The opt-in `-Image` / `--image` starter mode now uses one digest-pinned image for
the API and migrations without rebuilding. Fake-Docker tests cover both shell
launchers. Native source-built container installation and restore have passed;
fresh installation from the published registry digest and upgrade verification
remain pending. An offline, allowlisted standalone ZIP builder is available as a
[packaging preview](standalone-starter-packaging.md); preview bundles are
now published, but are not stable or install-certified official downloads.

The launcher foundation also validates resolved Compose configuration quietly
after `.env` initialization and before startup. Configuration failure blocks
that startup attempt; it does not certify an image, database or upgrade.

Fresh-environment initialization generates per-install encryption values instead
of copying shared sample values. Existing keys are preserved, not rotated. This
is a packaging prerequisite. The local bundle builder removes source-build
dependencies and host PostgreSQL exposure from its generated Compose file;
native source-built install/restore evidence is recorded below. Published-bundle
installation and stable release certification remain pending.

An offline pre-extraction verifier checks the whole ZIP against an independently
trusted SHA-256 and validates the bounded archive/manifest contract. It does not
replace signed release metadata or establish trust in an arbitrary supplied
checksum. The [candidate inspection](starter-image-candidate-20260927.md) records
an existing private image and the remaining local install-test prerequisites.

Robot OS and on-device software have a separate
[build and OTA delivery lane](robot-build-and-ota-plan.md), integrating with
the stock update process. Server container updates do not replace this work.

## Product and ownership boundaries

`openjibo.com` is the neutral revival-group entry point. It should promote the
community reviving Jibo and help owners choose a supported path, not funnel
every owner into a paid provider. `cloud.openjibo.com` is the separately operated
managed membership service. JiboExperiments owns the shared robot runtime and
its release tooling; JiboAutoMod owns conversion/onboarding orchestration.
Provider starters configure that runtime rather than fork it or reproduce the
commercial control plane. Do not redistribute proprietary robot images as part
of a runtime release; document supported owner-supplied recovery inputs.

The immediate delivery priority remains the closed production cloud preview.
Site content and offline packaging work can progress alongside it; public
downloads and network admission each require their own acceptance evidence.

## Neutral site delivery

Deliver an accessible, mobile-friendly site with:

- Revival-group mission, contributors, community/help and source links; use
  approved branding and clearly distinguish the revival project from Jibo, Inc.
- A "bring my Jibo back" journey for OOBE, v1/Pegasus and v2 robots, linked to
  the supported conversion matrix, prerequisites, recovery and AutoMod.
- A hosting comparison covering local requirements, offline behavior, security,
  ownership, network features and operational responsibility—not only price.
- Downloads and versioned documentation with stable/preview status, supported
  CPU/OS/host matrix, release notes, limitations and verified artifact identity.
- Provider selection that distinguishes official-runtime compatibility from
  endorsement, uptime guarantees or a provider's billing/account system.
- Upgrade, recovery, diagnostics, known issues and security-reporting paths.

Acceptance: all primary journeys work without a managed-service login; keyboard
navigation and small-screen layouts pass; download links correspond to tested
releases; unimplemented options are labeled planned rather than offered as live.
Select the public source repository/hosting and confirm domain/branding authority
before deployment. Do not move the neutral site into the private membership app.

## One runtime, three distribution profiles

| Profile | Package and operator path | Admission and network boundary |
| --- | --- | --- |
| Self-hosted isolated | Pinned source checkout plus current container starter; subsequently a tested prebuilt-image bundle requiring no source build | Owner-operated; independent of the managed control plane. HTTP or HTTPS is a deployment choice, not proof of robot count or permission to bypass authentication. |
| Self-hosted hybrid | Official runtime image and owner-fleet configuration/container starter | HTTPS, trusted-server enrollment and lifecycle/credential management; accepts only its owner's fleet. Network features must report degraded/offline states explicitly. |
| Managed provider | Same official runtime with provider deployment bundles and host adapters | HTTPS, approved network registration, controlled multi-owner admission, provider-neutral identity integration. Never bundle OpenJibo Cloud's private membership/billing code or operator secrets. |

Tokenless single-robot compatibility remains a separate explicit restricted
option, not the definition of self-hosting. A public self-hosted server is not
automatically a trusted ecosystem provider. A trusted build alone does not
authorize a server or prove customer ownership.

## Existing foundations to reuse

- `docker-compose.yml`, `Dockerfile` and `.env.example` define the source-built
  isolated stack and persistent database/media paths.
- `scripts/cloud/Invoke-OpenJiboSelfHostedStack.ps1` and
  `scripts/cloud/invoke-openjibo-self-hosted-stack.sh` initialize/run that stack.
- `.github/workflows/openjibo-cloud-ci.yml` (repository root) exercises runtime
  tests, Compose and fake-robot connectivity.
- `.github/workflows/openjibo-cloud-managed-deploy.yml` and `infra/azure/`
  provide the current Azure deployment foundation, not general multi-cloud
  provider installation or a public mirror service.
- [Self-hosted runbook](self-hosted-runbook.md) remains the current installation
  entry point until official binary bundles are published and tested.

## Release and package contract

Build once from a reviewed source commit. Promote the same tested artifacts;
do not rebuild different binaries under an existing version. A release should
include an immutable image digest, source commit/build provenance, signature,
checksums, dependency/license notices and SBOM, plus versioned starter bundles.
Publish only CPU architectures exercised by CI and a supported-host test. Local
speech/model variants must be explicit; do not promise ARM64 or every model
because the source can theoretically compile there.

A starter bundle contains versioned Compose/configuration templates, image
locks, platform and runtime compatibility metadata, migration requirements,
health/smoke checks and upgrade/recovery instructions. It contains no passwords,
private keys, user data or already-enrolled server identity. Generate credentials
per installation and persist them across upgrades; never silently regenerate
data-encryption keys. Isolated installation must not require a Cloud account.

Host adapters supply infrastructure/networking, HTTPS, secret injection,
storage, backup and process lifecycle—not host-specific forks of robot logic.
Start with generic Docker Compose/Linux and the existing Azure path. Track
AWS and Google Cloud adapters as separate future deliverables, not supported
targets until fresh install, persistence, update and recovery tests pass.
Additional hosts can implement the same documented adapter contract.

## Updates, mirrors and recovery

### Agreed installer and site sequence, 2026-10-01

Publish a neutral site preview with hosting choices, exact preview downloads,
limitations and isolated fresh-install instructions. A site is a useful test
entry point, not a technical prerequisite for testing a bundle. Keep
`openjibo.com` DNS unchanged until the public-host/domain cutover is reviewed.

Start with a targeted preview installer, tied to an exact bundle checksum and
image digest. Download, inspect, then run; do not recommend piping a remote script
into a shell. Once stable acceptance exists, the default channel selects the
latest approved stable release; `--version` selects a reproducible release and
`--channel preview` explicitly opts into experimental builds. No stable selector
is available before a stable release exists. Portable is the default; AVX2
requires explicit validation of the actual Docker runtime host.

The installer validates prerequisites and verified release metadata, initializes
fresh-install secrets, and starts pinned images in an unused project/directory.
It must not silently upgrade an existing stack, rotate keys or reuse live data.
Upgrades are a separate operator-reviewed command with backup, compatibility,
migration and recovery checks. Signed metadata/trust-root work remains a gate
before claiming a stable or mirror-safe installer.

Channels such as stable/preview resolve through authenticated release metadata
to immutable artifacts. A mutable `latest` tag is not an installation identity.
Use a reviewed signing/trust-root design with key rotation/revocation and
metadata expiry/version checks; checksum files on the same untrusted mirror
do not establish authenticity or prevent downgrade/replay attacks.

Mirrors replicate approved bytes and signed metadata. They are download
locations, not signing authorities: verify identity, signature and digest before
use and retain those checks after mirror fallback. Reject arbitrary redirect
hosts or insecure transport; stale metadata must not silently select an older
release. Test primary unavailable, corrupt mirror, stale/replayed metadata,
revoked signer and partially downloaded artifacts. Offline users need an
explicit verified import route with the last trusted metadata and a documented
freshness limitation—not an authentication bypass.

Default updates are operator-reviewed, not unattended migration. The updater
must inventory current version/config/data compatibility, preview the change,
check disk/resources, require verified backups, lock out concurrent updates,
verify/download artifacts, apply compatible migrations, start the candidate,
and run health plus robot protocol smoke before declaring success. Preserve
configuration, keys, identity and volumes. An old image is not a safe database
rollback after an incompatible migration: require a tested restore or forward
repair plan. Never run `docker compose down -v` as an upgrade/recovery shortcut.

## Issue-sized delivery queue

| Order | Deliverable | Exit evidence |
| --- | --- | --- |
| D1 | Source-to-prebuilt starter packaging foundation | Local/offline preparation and explicit digest launcher mode; rejects tags and keeps API/migrator images identical. No official image or downloadable bundle is implied, and no automatic publish/install occurs. |
| D2 | Official build/publish pipeline and trust metadata | Protected release approval, pinned source/digests, provenance/signatures/SBOM and tested architectures; tamper/replay checks pass. Registry and signing ownership approved. |
| D3 | Isolated download bundle and install smoke | Fresh Windows/Docker Desktop and Linux installs; authenticated robot protocol test; persistence/restart and backup/restore evidence; offline control-plane independence. |
| D4 | Neutral site and download catalog | Approved copy/branding, hosting/DNS, accessible journeys and only verified release links; provider choices remain neutral. |
| D5 | Hybrid starter and network lifecycle | Trusted registration/renewal/revocation, HTTPS, owner-fleet-only admission and outage behavior proven. |
| D6 | Managed-provider starter and host adapters | Multi-owner isolation, provider-neutral auth integration, generic/each cloud-host install and recovery matrix. |
| D7 | Update client, mirrors and support lifecycle | Signed/fresh metadata checks, mirror failover, locking/interruption recovery, migrations and restore rehearsal; supported-version/deprecation policy. |

Packaging does not remove the separate runtime reliability, ownership,
entitlement or provider admission gates. Mark each profile experimental until
its own exit evidence is retained; do not advertise all three as production-ready
because one image has been published.
For D3, D5 and D6, record the exact starter version, image digest, supported
host/platform and OOBE/v1/v2 device test matrix in each release's acceptance
record; success for one profile/device does not certify the others.

### Current local acceptance evidence

The [starter acceptance record](starter-acceptance-20260927.md) covers the local
Docker Desktop install, authenticated socket checks, persistence and separate
backup/restore rehearsal. The [speech acceptance record](speech-build-preflight-20260928.md)
adds an explicitly built local Whisper image, offline acoustic transcription,
same-socket repeated turns and cloud-response speech instructions. Neither is
an official published release. Independent native-Linux installation has since
passed the checks recorded below; actual robot speech playback remains a D3 gap.
Stable release trust remains D2 work. Do not infer hybrid or managed-provider certification from these isolated
tests.

### Native Linux progress, 2026-09-30

The [native Ubuntu record](native-linux-acceptance-20260928.md) now covers fresh
build/startup, authenticated sockets, restart persistence, separate database and
volume restore, and acoustic turns. The [AVX2 experiment](whisper-avx2-experiment.md)
records successful portable/AVX2 comparisons and a second synthetic phrase.
Actual robot microphone input and playback remain pending; development proceeds
with that gate visible. Broader speaker/noise coverage and encrypted user-data
restore fixtures also remain outstanding.

`openjibo-speech-build-preview.yml` adds manual build validation for Linux/amd64
portable and AVX2 candidates. It retains source commit, image ID, model checksum,
CMake flags and server executable smoke evidence. It does not push images,
publish downloads, sign artifacts, or deploy. CI evidence artifacts contain
metadata only; they are not installable packages. The first run passed both
profiles: https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/36769399458.
These checks cover compilation/executable startup, not acoustic behavior on CI.
Signing policy, SBOM/license review and stable release metadata remain D2
deliverables. Registry ownership and preview starter linkage are now in place.

Registry ownership is now settled: Transcendent-Software-LLC owns
`ghcr.io/transcendent-software-llc/openjibo-runtime`. The
[publication record](runtime-registry-publication.md) records successful portable
and AVX2 publication, digest-pinned starter ZIP verification, and independent
registry-pull/provenance verification. Package visibility was made public by the
owner, and anonymous manifest reads for both exact digests returned 200 with
matching identities. SBOM and stable release trust remain
pending. No stable/latest tag, production deployment or robot change was made.
