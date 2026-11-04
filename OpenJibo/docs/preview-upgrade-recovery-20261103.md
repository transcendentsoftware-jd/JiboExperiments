# Rehearse a preview upgrade and recovery

Use a separate synthetic installation before changing a server that holds real
robot data. Preparation verifies a new package; it does not upgrade an existing
installation. The native launcher is for fresh installations and must not be
used to regenerate keys over retained data.

This procedure targets the standard isolated PostgreSQL/File-media Compose
profile. Custom mounts, external databases, custom integration paths and live
robot traffic require their own recovery plan. The commands below use native
Linux paths. Windows fresh installation has separate acceptance; these commands
do not establish Windows upgrade acceptance.

## Retain the old installation first

Record the exact old image digest, signed package and source revision. Preserve
its Compose files, effective overrides, project name, PostgreSQL image/major
version and private `.env`. Keep a local copy of the old image available. A
mutable tag or a recorded image ID alone does not guarantee recovery.

Stop application, migration and external writers with the original project,
files and `--profile self-hosted`. Keep PostgreSQL running for the backup. Do
not run `down` or replace the API before exporting any container-local files.
Follow [backup and recovery](self-hosted-recovery.md), including the separate
encrypted integration-file export. A backup of `/data` does not capture the old
API's `/app/App_Data/user-integrations.json` writable-layer file.

Export that file to a new private destination. Verify the proposed volume
destination is absent, stage the original bytes into `/data`, copy them back
and compare hashes. Refuse an existing destination, including a dangling link.
Keep the original encryption passphrase and salt. Only after the byte comparison
passes, explicitly select
`OpenJibo__UserIntegrations__PersistencePath=/data/user-integrations.json` in the
old environment. Follow the full [credential preservation procedure](self-hosted-recovery.md#preserve-existing-integration-credentials-before-changing-their-path).

With the staged configuration effective and all writers stopped, capture and
verify a second full backup. Retain the original export, both backups and a
trusted copy of their hashes separately. This second backup is the pre-upgrade
recovery point: it contains the old runtime configuration, both databases and
the staged encrypted file in the API volume.

## Prepare and review the new package

Use the [current preview's signed preparation](durable-runtime-preview-20261103.md#prepare-without-starting-anything)
with explicit version and CPU profile, into a new private directory. Do not run
the fresh-key initializer or `Start` entrypoint there. Review the new package's
Compose settings against the retained old settings before starting services.

Copy the retained private environment into the new directory, preserving every
original secret. Published preview Compose files pin the API and migrator image
directly; editing an `OPENJIBO_RUNTIME_IMAGE` variable does not select a different
image in those files. Inspect the effective configuration privately and confirm
that both services select the intended new digest. Do not publish that output.

Keep the **same Compose project name** for the upgrade, so the existing named
volumes remain selected. Carry forward reviewed loopback-only port and speech
overrides. Do not blindly copy unrelated host or production overrides. For the
synthetic rehearsal, ensure no physical robot can send traffic to the instance.

After backup verification, a native Linux manual upgrade uses this command
shape, with all placeholders replaced by the reviewed installation values:

```text
docker compose --project-directory NEW_DIR --env-file NEW_DIR/.env -f NEW_DIR/docker-compose.yml -f REVIEWED_UPGRADE_OVERRIDE --profile self-hosted --project-name ORIGINAL_PROJECT up -d --no-build postgres migrate api
```

Check migration completion and health. Verify the actual API image digest and
container replacement, unchanged named volumes and original encryption keys.
Compare device IDs, canonical robot IDs, original creation times, loop links,
primary selection, personal memory and retained file bytes. Prove the encrypted
integration state can still be read without contacting real external services.
Then exercise issued-token sockets and recorded local speech. Repeat the
persistence checks after a real down/up with the selected profile and retained
volumes. A health response alone does not prove data continuity.

## Recover under the old version in a separate project

Stop the upgraded rehearsal's services. Use the **pre-upgrade** backup and old
signed package in a new project with fresh, distinct API and PostgreSQL volumes
and a different loopback port. Preserve the old configuration and encryption
keys. Restore both database dumps with exit-on-error into empty databases, then
restore the API volume before starting the old API. Use the same PostgreSQL
major version and reviewed image as the backup.

Follow the [separate-installation recovery steps](self-hosted-recovery.md#rehearse-recovery-into-a-separate-installation).
Verify original identity timestamps, loop associations, memory and binary file
bytes before API startup. Run the old package's migrations and API, confirm its
exact old digest and keys, then check health, original primary selection,
issued-token sockets and recorded speech. Read the restored encrypted state
using the old runtime's persistence code as well.

Do not point old binaries at the upgraded database to simulate rollback.
Selecting an older image does not undo migrations. Never use `down -v` or reuse
the source project's volumes for a recovery rehearsal. Stop test services when
finished and retain keys, volumes, exports and verified recovery material.

## Acceptance record

Both exact profiles passed on native Ubuntu 24.04.5 x86_64 with an i5-8250U/8GB,
Docker 29.8.2 and Compose 5.6.0. The source installation used
[`runtime-preview-38165346370`](guarded-source-preview-publication-20261011.md),
source `b9dd0de89dfdb7bea558c2ae0923eefd50758b52`. The target used
[`runtime-preview-41729015780`](durable-runtime-preview-20261103.md), source
`ac0b30b11025944cb0e88d4a3437c39f07307a8d`. Both packages underwent actual
source-bound signed preparation; no signature or manifest overrides were used.

| Profile | Passed checks | Old / upgraded / restored recorded turn times, ms |
| --- | ---: | --- |
| Portable | 55 | 6,896 / 6,445; 6,266 / 6,307; 5,584 / 6,588 |
| AVX2 | 52 | 624 / 506; 595 / 546; 638 / 511 |

All 12 synthetic recorded turns returned the expected cloud-version transcript
and instruction with Azure Speech disabled. These are bounded fixture times,
not an SLA. Both upgrade paths preserved three original device IDs, canonical
robot IDs, creation timestamps, loop associations, primary selection, personal
memory and encrypted integration credentials. The API was actually replaced
with the target digest, using the original volume and keys; a subsequent
down/up replaced it again and retained identities and credentials.

Old-image recovery used fresh, distinct API and database volumes and the
pre-upgrade backup. An independent read-only audit compared both the upgraded
and recovered databases with the archived device/loop rows. Canonical IDs,
original timestamps and primary flags matched. Ciphertext read back from both
stopped APIs matched the original export. The old runtime read restored
credentials, while the current runtime refused wrong keys without changing
bytes and then read the original secret and feed with the correct keys.
Credential probes injected only a small helper executable and used each
image's shipped production assemblies and dependency/runtime configuration;
they did not replace the persistence library. No real integrations were called.

The old image returned `Loop scope was not found` on each first fresh synthetic
registration after committing the device/loop rows. Retries succeeded after
those rows were independently checked. This is retained as an old-version
registration finding, not counted as a successful first-attempt OOBE deployment.
Harness corrections for missing preflight input, speech identity naming, image
assembly layout and an assumed image environment variable also retained their
original failure logs and resumed existing state without resetting data/keys.
The portable count includes three additional resume-health checks.

All new services are stopped with private keys, volumes, original exports and
backups retained. All 18 previously running containers remained running without
restart. Only redacted metadata was copied from the host. The release's
[supplemental upgrade/recovery report](https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/runtime-preview-41729015780/upgrade-recovery-acceptance.json)
and `UPGRADE-RECOVERY-SHA256SUMS` preserve these observations. These supplemental
records are not additional signed attestation subjects. Original release assets,
image digests, default selectors and deployments are unchanged.

This guide does not certify general upgrade compatibility,
owner isolation, physical OOBE deployment, robot playback or stable release
readiness. A synthetic credential read is not an end-to-end Home Assistant or
calendar integration test.
