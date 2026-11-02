# Choose a hosting and conversion path

Status: November 2026. This page connects the existing tools and records the
remaining delivery work. The runtime is running on managed infrastructure;
public paid enrollment and complete automatic conversion are still gated.

## Start here

| Your goal | Starting point | What you operate |
| --- | --- | --- |
| Use managed hosting | [Managed service](https://cloud.openjibo.com), subject to its enrollment status | Your robot and account; the provider operates hosting |
| Host on your own computer or server | Host check below, then the [self-hosted runbook](self-hosted-runbook.md) | Runtime, database, secrets, backups, updates and network access |
| Convert or recover a robot | [Conversion and device preparation](#conversion-and-device-preparation) | A separate USB/device procedure, followed by a selected hosting target |
| Develop the runtime | [Local cloud quickstart](local-cloud-quickstart.md) | Source-built development services |

Self-hosting does not require buying managed membership. Hybrid owner-fleet and
multi-owner provider admission have separate trust and identity requirements;
starting a container does not enroll either. Choose the hosting target before
planning device changes.

## Host and architecture matrix

The operating system running the tools, the Docker engine's operating system
and CPU, and Jibo's installed software are separate compatibility dimensions.
Linux containers are required even when the computer runs Windows or macOS.

| Tool computer | Existing self-hosting entry point | Current evidence and limit |
| --- | --- | --- |
| Linux x86-64 | Bash Compose launcher; reviewed published-preview preparer/launcher | Native install, persistence, restore and speech evidence exists for specific previews. See [acceptance](published-starter-acceptance-20261006.md) and the exact release record. No blanket stable certification. |
| Windows | PowerShell Compose source/digest launcher with a Linux Docker engine | Launcher contracts are tested. A complete fresh Windows install, update, restore and physical-robot rehearsal remains required. The published-preview Python launcher currently refuses this host. |
| macOS Intel | Bash Compose source/digest launcher with a Linux Docker engine | Script path exists; complete native host acceptance remains required. The published-preview Python launcher currently refuses this host. |
| macOS Apple Silicon or Linux ARM64 | Tooling can inspect the selected engine | The current reviewed preview images are Linux/amd64. No native ARM64 image or emulation acceptance is established; do not silently select emulation or label it supported. |

Portable and AVX2 are CPU build profiles, not operating systems. Portable is the
default preview; AVX2 requires checks on the actual engine host. A remote Docker
context may use a different OS or CPU from the computer where the command runs.
The [guarded-source preview record](guarded-source-preview-publication-20261011.md)
binds the current experimental download to its exact source and digest and
describes its synthetic-test limits. Older preview evidence does not certify a
new image or operating system.

## Check prerequisites first

From a trusted source checkout's `OpenJibo` directory, use Python 3.9 or newer:

```powershell
# Windows
py -3 scripts/cloud/inspect-openjibo-host.py
```

```bash
# Linux or macOS
python3 scripts/cloud/inspect-openjibo-host.py
```

The checker queries `docker compose version --short` and `docker version` on
the selected Docker context, with a five-second deadline for each query. It
does not initialize `.env`, download images, build, start containers, migrate a
database or contact a robot. Docker itself may contact its configured engine.
It reports only allowlisted status and platform fields; it does not print
environment variables, daemon responses, paths or configuration secrets.

Exit `0` means these prerequisites passed. Exit `2` means follow the reported
next action and rerun the check. Neither exit certifies image trust, available
resources, AVX2, networking, installation, data recovery or conversion.
Use `--timeout 15` for a slow engine, up to 30 seconds per query. Use `--json`
for a redacted report that can accompany a support request.

## Start self-hosting

Continue with the [self-hosted runbook](self-hosted-runbook.md) to initialize
per-install encryption values, set the database password, retain recovery
material and choose source or digest mode. The existing startup commands are:

```powershell
# Windows / PowerShell, from OpenJibo
.\scripts\cloud\Invoke-OpenJiboSelfHostedStack.ps1 -RunMigration
```

```bash
# Linux / macOS / Bash, from OpenJibo
bash scripts/cloud/invoke-openjibo-self-hosted-stack.sh --run-migration
```

These commands perform installation work: they may create `.env`, build images,
start services and run migrations. Configure the runbook's required values first.
Both launchers quietly validate Compose configuration before startup. Keep the
encryption keys with the database backup; an upgrade must preserve existing keys.

For a reviewed prebuilt image, the same launchers accept `-Image` or `--image`
with an exact `registry/repository@sha256:<64 lowercase hex>` reference; tags
are rejected. Use a release's verified identity and instructions rather than
guessing a current digest. Avoid `--skip-build` / `-SkipBuild` in digest mode.

The targeted [published-preview preparation and launch path](published-starter-provenance-20261006.md)
verifies reviewed release identities and provenance, prepares a new directory,
and requires an explicit start. Its launcher is **native Linux only** today.
The [download page](https://transcendentsoftware-jd.github.io/JiboExperiments/downloads.html)
identifies the selected experimental preview. Preparation and host checks are
not permission to upgrade an existing installation or bypass the native-host guard.

After startup, follow the runbook's health, database persistence, backup/restore,
network and device checks. Keep a known-working image and matching recovery
material before upgrading. HTTP isolated operation and tokenless compatibility
are separate choices described in the [single-robot guide](single-robot-http-self-hosting.md).

## Conversion and device preparation

JiboAutoMod owns USB/device mutation; this runtime repository owns server and
protocol configuration. Start from its [repository and current instructions](https://github.com/Jibo-Revival-Group/JiboAutoMod)
and this repository's [device bootstrap](device-bootstrap.md) and
[mode-conversion plan](open-jibo-mode-conversion-plan.md).
Use instructions for the actual selected branch and tool version: OS entry
points and recovery methods differ. The existence of Linux, macOS or Windows
code is not a completed hardware support claim.

The easy-button product must connect these stages in one guided flow:

1. Inspect host prerequisites and robot release/partition state without writes.
2. Select isolated self-hosting, hybrid owner-fleet or a named managed provider.
3. Create immutable device-specific recovery material and show the exact plan.
4. Apply the selected, confirmed profile through the proven bounded writer.
5. Read back changes, verify provider/identity, then exercise API, socket and speech paths.
6. Export redacted diagnostics and provide verified rollback or provider switching.

The firewall/access tool alone is not complete conversion. Do not silently
choose a paid provider, infer cloud ownership from legacy credentials or make
offline conversion depend on the managed service. Preserve personal and
calibration data. Unknown device layouts require an audit result, not guessed writes.

## Delivery checklist

| Milestone | Acceptance required before calling it delivered |
| --- | --- |
| Live managed platform | Verified public origin/sign-in, durable ownership assignment, runtime credential enforcement, recovery and support; metering/entitlement/subscription gates before paid enrollment |
| Windows self-hosting | Fresh Linux-engine install, startup, real device sockets/speech, restart persistence, isolated restore and update/rollback |
| macOS self-hosting | Same host acceptance; record Intel and Apple Silicon separately, including actual engine architecture |
| ARM64 | Reproducible native image, provenance, speech/model compatibility and complete host acceptance; emulation is a separately labeled experiment |
| Easy conversion | Host/robot compatibility matrix, audit/plan/export, immutable recovery, bounded apply/readback, provider handoff and hardware rollback evidence |
| Documentation | Versioned OS instructions, exact tested release identity, prerequisites, recovery, redacted support export and known issues linked from the main entry points |

Keep the full distribution queue in [official distribution](official-distribution-plan.md)
and robot build/OTA work in its [separate plan](robot-build-and-ota-plan.md).
Container updates, server installation and robot OS conversion require their
own tests and recovery procedures.
