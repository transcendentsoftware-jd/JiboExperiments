# Capture and check a self-hosted recovery backup

From a trusted checkout, `scripts/cloud/backup-self-hosted.py` captures the
standard isolated Compose deployment on Windows, macOS or Linux with Python
3.9+ and Docker/Compose. It exports both PostgreSQL databases in custom format,
the complete API `/data` volume as a tar stream, the selected `.env` bytes and
the containers' effective environment and image IDs. Binary transfers bypass
shell redirection and text encoding.

The output contains passwords, encryption keys, user data and media. Use a
private backup directory with access restricted to the operator. The tool
requests mode 0700 when creating it; on Windows, secure the parent with Windows
permissions. It does not encrypt backups, configure ACLs or upload anything.
Do not commit the directory or attach it to a public support request.

## Capture

Schedule downtime. Stop robot traffic, any external database clients and other
writers. Stop the selected project's application and migration containers while
leaving PostgreSQL running. Use the same project name, environment file and
Compose files as the installation. For a default checkout deployment:

```text
docker compose --project-name my-jibo --env-file .env --profile self-hosted stop --timeout 90 api migrate
```

From `OpenJibo`, run the following command as one line (`python3` on macOS/Linux,
or `py -3` on Windows):

```text
python3 -B scripts/cloud/backup-self-hosted.py capture --directory . --project-name my-jibo --env-file .env --compose-file docker-compose.yml --writers-stopped --output /private/backups/jibo-20261103
```

Replace paths and the project name. The output parent must exist; the output
directory must be new. Repeat `--compose-file` for each installation override in
its original order. The script never starts/stops services, runs migrations,
restores a database, rotates keys or removes volumes. Resume the selected stack
with its usual launcher after capture, preserving the same project and keys.

The script requires one stopped API, one stopped migration container and one
running PostgreSQL container with the standard named volumes. Custom connection
strings, alternative media locations, bind/nested API mounts and other running
containers mounting these volumes are refused. It captures effective container
environment separately because process environment can override `.env`; these
values must be reviewed and retained for recovery. The saved `.env` alone may
not describe the deployment. Custom images/configuration remain the operator's
responsibility; this is not detection of every external writer or storage path.

Both database dumps use PostgreSQL's logical backup snapshots. They are separate
snapshots, so all application and external writers must remain stopped for the
whole capture. The tool checks container identity/state/configuration before
and after transfers; it cannot prevent an external connection or guarantee a
cross-database atomic snapshot. `--writers-stopped` acknowledges that condition.

Only a completed capture writes `manifest.json`. A failed transfer retains an
incomplete directory for private inspection; do not treat it as a backup or
reuse it as the next output destination. Each transfer has a timeout (default
300 seconds, adjustable with `--timeout`, maximum 3600).

## Verify bytes before recovery

Retain the successful command's `manifestSha256` independently, along with the
release/image provenance. Verification streams member hashes without invoking
Docker, reading tar contents, extracting files or executing SQL:

```text
python3 -B scripts/cloud/backup-self-hosted.py verify --backup /private/backups/jibo-20261103 --expected-manifest-sha256 RETAINED_MANIFEST_SHA256
```

It checks the exact inventory and every member's size/hash. Hashes establish
agreement with the separately retained checksum, not publisher identity or
restorability. Keep a protected offline copy; a checksum stored only beside a
rewritable backup does not provide independent integrity evidence.

## Rehearse recovery into a separate installation

The command deliberately provides no automatic restore. Before an upgrade,
rehearse the following on a new Compose project with fresh, separate volumes,
loopback-only ports and robot traffic disconnected:

1. Verify the packet and retain a trusted copy of the original runtime image,
   Compose configuration and matching PostgreSQL major version. Image IDs are
   observations, not an image archive or a guarantee of future availability.
2. Prepare recovery configuration from the saved `.env` and effective private
   environment. Preserve the original encryption passphrase and salt exactly.
   Review passwords and any inherited overrides; do not run the fresh-key
   initializer over recovery data. Do not reuse the original project's volumes.
3. Start only the new PostgreSQL service. Restore `state.dump` into
   `openjibo_state` and `memory.dump` into `openjibo_memory` using `pg_restore`
   with `--exit-on-error --no-owner --no-acl`. Restore only into fresh empty
   databases. Do not use a shell text pipeline for custom-format dump bytes.
4. Create the new API container without starting it. Restore the trusted
   `api-data.tar` into its separate `/data` volume, retaining ownership and file
   contents. Do not extract untrusted archives on the host.
5. Run the reviewed image's migrations, start the new API, then check health,
   robot/loop identities, default selection, personal memory and file content.
   Prove encrypted content can be read with the retained keys. Run issued-token
   socket checks against the recovery instance and repeat after a full restart.
6. Stop the rehearsal services and retain evidence before considering an
   upgrade or switching robot traffic. Selecting an older image alone does not
   undo database migrations.

This procedure does not restore robot-local calibration, firmware, enrollment
tokens held only in API process memory, external services or provider state.
It does not capture the container writable layer outside `/data`. For example,
older installations may keep user-integrations JSON under `/app/App_Data`.
Fresh `.env` files prepared from the current template select
`OpenJibo__UserIntegrations__PersistencePath=/data/user-integrations.json`,
which is covered by the API volume backup. Initialization preserves existing
`.env` files and does not silently relocate their integration state. Retain
container-local files separately and migrate them before replacement as below.
Physical OOBE compatibility and live robot recovery need separate validation.

## Preserve existing integration credentials before changing their path

This is an operator migration for the standard Linux Compose API image, not an
automatic upgrade. It changes no database schema. Keep the old container until
the original file and configuration have been retained and the new copy has
been checked. Do not run `down`, recreate the API, or replace `.env` first.

1. Stop application, migration and external writers using the installation's
   project/files. Leave PostgreSQL running for the full backup. Identify the
   existing API container with `docker compose ps --all --quiet api` using those
   same project/files. Inspect its effective integration path and encryption
   configuration privately. An unset integration path uses
   `/app/App_Data/user-integrations.json`; a custom path needs separate review.
2. Copy the existing encrypted file to a **new private host destination** with
   `docker cp STOPPED_API_ID:/app/App_Data/user-integrations.json PRIVATE_ORIGINAL`.
   If this fails, stop: a failed copy does not establish that no credentials
   exist. Resolve the source path/access first. If this is a fresh installation
   with independently confirmed absent integration state, use the fresh setup
   flow instead of inventing an empty replacement for an unknown file.
3. Retain a full backup and its effective configuration/keys. Record the original
   encrypted file's SHA-256 privately with `Get-FileHash` on Windows or
   `sha256sum`/`shasum -a 256` on Linux/macOS. Do not print its contents or paste
   the effective environment into support reports.
4. Confirm `/data` is the existing API named volume and that its proposed
   `user-integrations.json` destination is absent. For the standard image, the
   following check uses its exact locally installed image ID and the stopped
   API's mounts; replace the placeholders and run as one line:

   ```text
   docker run --rm --pull=never --network none --volumes-from STOPPED_API_ID --entrypoint /bin/sh EXACT_LOCAL_IMAGE_ID -c 'test -d /data && test -r /data && test -x /data && test ! -e /data/user-integrations.json && test ! -L /data/user-integrations.json'
   ```

   Any failure means stop and inspect privately. Do not overwrite an existing
   destination, including a dangling link. If a destination already exists,
   retain it separately and resolve which state is authoritative. Keep all
   writers stopped between this check and the copy.
5. Copy the retained original to the existing mounted volume:
   `docker cp PRIVATE_ORIGINAL STOPPED_API_ID:/data/user-integrations.json`.
   Copy it back to another new private host file and compare its SHA-256 with
   the retained original. Continue only on an exact match. The original
   container-local file and private export remain untouched.
6. Add `OpenJibo__UserIntegrations__PersistencePath=/data/user-integrations.json`
   to the existing `.env`, keeping the original encryption passphrase and salt.
   Review inherited process overrides too. Capture another full backup so the
   staged file and changed configuration are retained. Then start/recreate the
   API with the reviewed image and normal launcher, preserving project/volumes.
7. Verify the existing Home Assistant link/command secret and calendar feeds
   through the application, then repeat after container replacement. Rehearse
   restoring that volume and the original keys into a separate installation.
   Retain all exports until this succeeds; do not reconnect real integrations
   merely to fabricate a successful test.

Current source refuses to load an existing unreadable integration file. Wrong
keys, invalid ciphertext, unsupported schema or malformed data produce a load
error and preserve the original bytes. Restore matching keys or a known-good
backup and retry. A genuinely missing file starts empty without writing until
an explicit integration change. Older runtime images may still reset unreadable
files; the configuration setting alone does not change their loading behavior.
