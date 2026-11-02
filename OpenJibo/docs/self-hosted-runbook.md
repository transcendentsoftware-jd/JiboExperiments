# Self-Hosted OpenJibo Runbook

## Purpose

This is the single starting point for self-hosting the OpenJibo cloud with Docker Compose on your own hardware. Other docs cover specific pieces in more depth; this runbook tells you which one to read next for each step.

- [scripts/cloud/README.md](../scripts/cloud/README.md) — full inventory of every script in the repo, including the managed/Azure path. Use it as a reference, not a starting point.
- [docs/single-robot-http-self-hosting.md](single-robot-http-self-hosting.md) — deep dive on robot token/network overrides and the tokenless single-robot compatibility mode.
- [docs/local-cloud-quickstart.md](local-cloud-quickstart.md) — running the `.NET` cloud directly with `dotnet run` instead of Docker, useful for development.
- [docs/device-bootstrap.md](device-bootstrap.md) — pointing a physical Jibo at your self-hosted server.
- [Standalone starter packaging preview](standalone-starter-packaging.md) — offline preparation for a fresh digest-pinned installation; not yet an official download or verified upgrade path.

## 1. Prerequisites

Check the [OS/architecture matrix and read-only host checker](platform-setup-guide.md#check-prerequisites-first)
before installation. Windows and macOS have Compose script paths, but still
need complete host acceptance; the published-preview Python launcher is native
Linux only. Current preview image evidence is Linux/amd64.

Install:

- Docker and Docker Compose (`docker compose` v2 CLI)
- PowerShell, if you prefer the `.ps1` scripts over the `.sh` equivalents
- OpenSSL for the Bash initializer's cryptographic random generation (the
  PowerShell initializer uses .NET's cryptographic random generator)

Everything else — .NET, ffmpeg, whisper.cpp — is built into the container image. You do not need to install any of those on the host.

## 2. Configure `.env`

From the `OpenJibo` repo root:

```powershell
.\scripts\cloud\Initialize-OpenJiboComposeEnv.ps1
```

```bash
./scripts/cloud/initialize-openjibo-compose-env.sh
```

For a new installation, this prepares `.env` from the example and replaces its
sample encryption passphrase and salt with independently generated random
values. Generation/template failure must leave no final `.env`. Values are not
printed. Existing `.env` encryption values are never automatically rotated.
Then edit `.env` and set at minimum:

- `OPENJIBO_POSTGRES_PASSWORD` — required; the stack will not start without it.
- `OPENJIBO_USER_ENCRYPT` / `OPENJIBO_USER_SALT` — generated for new installs;
  retain and securely back up these values with your database recovery material.
  Do not change them after your first run: existing data requires the original values.
- `OPENJIBO_SEARCH_BACKEND` / `OPENJIBO_SEARCH_FALLBACK` — optional knowledge-search backend (Wolfram, ChatGPT, Ollama). Leave as `none` to disable.

If you manually copy `.env.example`, replace its sample encryption values before
the first run. If an existing installation already used those samples, do not
delete `.env` or regenerate keys against that database. Rotation requires a
separate, tested data migration/re-encryption procedure; it is not implemented
by initialization. Restore the matching original keys when restoring a database.
Inherited process environment can override Compose's `.env`; ensure it does not
silently supply different encryption values.

Speech-to-text options (see [section 4](#4-speech-to-text-local-whisper-vs-azure-speech) below):

- `OPENJIBO_ENABLE_LOCAL_WHISPER` — defaults to `true`. Set to `false` if you only plan to use Azure Speech.
- `OPENJIBO_WHISPER_MODEL` — defaults to `base.en`. Any model name accepted by whisper.cpp's `download-ggml-model.sh`.

## 3. Build and start the stack

```powershell
.\scripts\cloud\Invoke-OpenJiboSelfHostedStack.ps1 -RunMigration
```

```bash
./scripts/cloud/invoke-openjibo-self-hosted-stack.sh --run-migration
```

This builds the `api`/`migrate` image (installing whisper.cpp and its model as part of the build, unless disabled), starts PostgreSQL, applies migrations, and starts the API on port `8080`.

Before startup, both launchers initialize the local `.env` and run
`docker compose config --quiet` from the checkout root. A configuration failure
stops the launcher before its `compose up` call, so that invocation does not
build/pull images, start containers or run migrations. Initialization may already
have created or updated `.env`; this is not a side-effect-free dry run. The quiet
check avoids printing the resolved configuration and its secrets. Do not paste
an unredacted `docker compose config` output into support reports.

This checks Compose configuration, not image provenance/availability, Docker
daemon health, database passwords, schema compatibility or safe upgrade/restore.
Existing containers are not stopped by a failed check. Keep the backup and
release-verification steps below even when preflight succeeds.

The launcher suppresses Compose diagnostics during this check as well as its
resolved output. For local troubleshooting, run `docker compose config --quiet`
from the same checkout with the same environment, and review errors privately.
Check Docker/Compose availability, YAML syntax, referenced files and required
variables first. Redact credentials before sharing any diagnostics.

- Use `-SkipBuild` / `--skip-build` on later runs if you have not changed the Dockerfile, `.env` whisper settings, or source.
- Migrations are a required, idempotent startup dependency: Compose runs `migrate` before `api` on every stack startup, and already-applied migration scripts are skipped by the migration ledger.
- `-RunMigration` / `--run-migration` explicitly targets the migration service for visibility and compatibility with the initial bring-up/retry procedure; it is not a switch that disables migrations. Use `-SkipBuild` / `--skip-build` for repeat starts when the image does not need rebuilding.

Rebuild is required any time you change `OPENJIBO_ENABLE_LOCAL_WHISPER` or `OPENJIBO_WHISPER_MODEL`, since those are Docker build arguments, not just runtime environment variables.

Local Whisper compilation defaults to two concurrent jobs instead of the host's
CPU count. `WHISPER_BUILD_JOBS` accepts 1–32; available RAM, not CPU count alone,
should determine the value. This bounds the Whisper compile, not every .NET or
Docker build process. For a manually prepared preview image, for example:

```text
docker build --build-arg ENABLE_LOCAL_WHISPER=true --build-arg WHISPER_MODEL=base.en --build-arg WHISPER_BUILD_JOBS=1 -t openjibo-cloud:speech-preview .
```

This builds locally and does not publish or start anything. The local tag is not
a verified digest reference for distribution. The CPU build disables native-host
tuning and newer x86 feature requirements conservatively; this may reduce speech
performance and has not yet established a supported old-hardware matrix.
Inspect the resulting executable/model and test real audio before promotion.
See [speech build preflight](speech-build-preflight-20260928.md).

### Prebuilt-image starter foundation

The launchers also accept an explicitly selected image digest. This is an
expert/operator path, **not a published official download or automatic updater**.
It still uses this checkout's Compose and initialization files. Match the
checkout/configuration to the image's documented release and verify its source,
signature, architecture and speech/model variant through a trusted release
channel before running it. A digest pins bytes; it does not prove who built them.
Official release signing, bundles and mirrors are tracked in the
[distribution plan](official-distribution-plan.md).

After completing the backup/configuration checks below, replace the placeholder
with a verified `registry/repository@sha256:<64 lowercase hex>` reference:

```powershell
.\scripts\cloud\Invoke-OpenJiboSelfHostedStack.ps1 -RunMigration -Image '<verified image digest reference>'
```

```bash
./scripts/cloud/invoke-openjibo-self-hosted-stack.sh --run-migration --image '<verified image digest reference>'
```

Both API and migration services use the same image. This path disables builds
and pulls missing images; it does not build a missing image from your checkout.
Mutable tags, malformed references and combining the image option with
`-SkipBuild`/`--skip-build` are rejected before environment initialization.
Source-build mode remains the default and selects the local
`openjibo-cloud:self-hosted` image even if an image override was inherited.
PowerShell restores the caller's image environment setting afterward.

Build-time Whisper options do not change a prebuilt image's contents. Select a
matching model/configuration rather than assuming a runtime environment setting
will install it. Running a different image can apply migrations: retain both
database backups and encryption keys, review schema compatibility and the
release's restore plan first. Merely choosing the old image is not a guaranteed
database rollback. These launchers do not perform signature verification,
trusted-network enrollment, mirror selection or unattended updates.

## 4. Database backups and migrations

The PostgreSQL volume is persistent. Before applying a schema migration or restoring data, make a plain-SQL backup of both databases and keep the files outside Docker's managed volumes:

```powershell
New-Item -ItemType Directory -Force backups | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
docker compose exec -T postgres pg_dump -U openjibo -d openjibo_state > "backups\openjibo_state-$stamp.sql"
docker compose exec -T postgres pg_dump -U openjibo -d openjibo_memory > "backups\openjibo_memory-$stamp.sql"
```

```bash
mkdir -p backups
stamp="$(date -u +%Y%m%d-%H%M%S)"
docker compose exec -T postgres pg_dump -U openjibo -d openjibo_state > "backups/openjibo_state-${stamp}.sql"
docker compose exec -T postgres pg_dump -U openjibo -d openjibo_memory > "backups/openjibo_memory-${stamp}.sql"
```

Protect these files as production data. Do not commit them or leave them in a shared directory.

For a migration review, the existing wrappers support a non-mutating preview. Run them from the repository root with the PostgreSQL connection environment set to the exposed Compose database (`localhost:5432`) and the same credentials as `.env`:

```powershell
$env:OPENJIBO_POSTGRES_HOST = "localhost"
$env:OPENJIBO_POSTGRES_PORT = "5432"
$env:OPENJIBO_POSTGRES_USER = "openjibo"
$env:OPENJIBO_POSTGRES_PASSWORD = "<the value from .env>"
.\scripts\cloud\Invoke-OpenJiboMigration.ps1 -Target all -Preview
.\scripts\cloud\Invoke-OpenJiboMigration.ps1 -Target all
```

```bash
export OPENJIBO_POSTGRES_HOST=localhost
export OPENJIBO_POSTGRES_PORT=5432
export OPENJIBO_POSTGRES_USER=openjibo
export OPENJIBO_POSTGRES_PASSWORD='<the value from .env>'
./scripts/cloud/invoke-openjibo-migration.sh --target all --preview
./scripts/cloud/invoke-openjibo-migration.sh --target all
```

The normal containerized apply path is the stack launcher documented in section 3 (`-RunMigration` / `--run-migration`). Review the preview output and confirm the backups exist before applying. Do not use legacy import switches unless you are intentionally importing a preserved legacy snapshot.

If migration fails, migrations are transactional. Do not delete the volumes. Inspect the failure and retry after correcting the cause:

```text
docker compose ps -a
docker compose logs --no-color migrate
docker compose logs --no-color postgres
```

Then rerun the launcher with `-RunMigration` / `--run-migration` (and `-SkipBuild` / `--skip-build` when the image does not need rebuilding). If the backup must be restored, stop `api`, restore the corresponding SQL dump into its matching database with `psql`, verify both databases, and rerun the migration. Keep the pre-migration dumps until the API and robot inventory have been verified.

### PostgreSQL password changes

Changing `OPENJIBO_POSTGRES_PASSWORD` in `.env` does not change the password stored inside an existing PostgreSQL volume; the init environment is only applied when the database is first initialized. That mismatch causes `28P01: password authentication failed` from `migrate` and `api`.

For a safe rotation, stop the API, connect through the local Compose socket, and use `psql`'s interactive password command so the new password is not placed in shell history:

```text
docker compose stop api
docker compose exec postgres psql -U openjibo -d openjibo_state
\password openjibo
\q
```

After `\password` succeeds, update `OPENJIBO_POSTGRES_PASSWORD` in `.env` to the same value, then recreate the API with the documented launcher (`-SkipBuild` / `--skip-build`). Test `/health` before reconnecting robots. If the old role password is unavailable, restore from a known-good backup or use an administrative PostgreSQL role; do not destroy `postgres-data` as a password-recovery shortcut.

> **Data-loss warning:** `docker compose down -v` removes the persistent `postgres-data` and `api-data` volumes. Use ordinary `docker compose down` when stopping the stack. Use `down -v` only when intentionally resetting the entire self-hosted installation and after confirming backups.

## 5. Speech-to-text: local Whisper vs Azure Speech

By default, the container image builds [whisper.cpp](https://github.com/ggml-org/whisper.cpp) from source and downloads the `base.en` model at build time, landing both at `/usr/bin/whisper.cpp/` inside the image. `docker-compose.yml` wires this up automatically:

- `OpenJibo__Stt__EnableLocalWhisperCpp=true`
- `OPENJIBO_STT_WHISPER_CLI_PATH=/usr/bin/whisper.cpp/build/bin/whisper-cli`
- `OPENJIBO_STT_WHISPER_MODEL_PATH=/usr/bin/whisper.cpp/models/ggml-<model>.bin`

No extra setup is needed for local transcription to work out of the box.

To use a different model, set `OPENJIBO_WHISPER_MODEL` in `.env` (e.g. `small.en`) and rebuild.

To skip local Whisper entirely and rely only on Azure Speech (smaller image, faster build, no local CPU transcription cost), set in `.env`:

```dotenv
OPENJIBO_ENABLE_LOCAL_WHISPER=false
```

Then also set the Azure Speech settings so a working STT path is actually available:

```dotenv
OpenJibo__Stt__EnableAzureSpeech=true
OpenJibo__Stt__AzureSpeechRegion=<your-region>
OpenJibo__Stt__AzureSpeechSubscriptionKey=<your-key>
```

Rebuild after changing `OPENJIBO_ENABLE_LOCAL_WHISPER` since it controls which Docker build stage runs.

## 6. Verify the stack

```powershell
.\scripts\cloud\Invoke-CloudSmoke.ps1 -BaseUrl http://localhost:8080 -TargetMode open-jibo-self-hosted
```

```bash
./scripts/cloud/invoke-cloud-smoke.sh --base-url http://localhost:8080 --target-mode open-jibo-self-hosted
```

Or just hit the health check directly:

```powershell
Invoke-RestMethod http://localhost:8080/health
```

## 7. Point a physical Jibo at the stack

See [docs/single-robot-http-self-hosting.md](single-robot-http-self-hosting.md) for the robot-side override JSON, the tokenless single-robot compatibility flag, and common `WebSocket Exception: Not authorized` / DNS troubleshooting. See [docs/device-bootstrap.md](device-bootstrap.md) for the device bootstrap steps themselves.

## Troubleshooting

- **`api` logs show a whisper-cli launch failure or missing model** — confirm the image was actually rebuilt after any `.env` whisper changes (`-SkipBuild`/`--skip-build` skips this). Check `docker compose logs api` for the resolved paths.
- **Build is slow or fails compiling whisper.cpp** — set `OPENJIBO_ENABLE_LOCAL_WHISPER=false` and use Azure Speech instead, or lower `OPENJIBO_WHISPER_MODEL` to a smaller model.
- **`migrate` never completes / `api` never starts** — check `docker compose logs postgres` and `docker compose logs migrate`; the `api` service depends on both `postgres` (healthy) and `migrate` (completed successfully).
- **Encryption errors after restoring data** — `OPENJIBO_USER_ENCRYPT`/`OPENJIBO_USER_SALT` must exactly match the values used when the data was written.
