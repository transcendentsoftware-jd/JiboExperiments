# OpenJibo

OpenJibo is the working revival track for Jibo.

We are rebuilding the hosted cloud first, then using that foundation for OTA, Open Jibo OS, and a tiered brain that can eventually hand higher-order work to CoffeeBreak without losing Jibo's original charm.

## Current Focus

- ship a stable Azure-hosted replacement cloud
- keep the Node prototype as the protocol oracle and capture harness
- port the production path to .NET
- support real devices through repeatable bootstrap steps first
- use OTA later to reduce recovery friction once the cloud is trustworthy
- keep durable architecture decisions in `docs/architecture/` so turn/state, storage, and trust choices do not drift out of the active docs

Current release truth lives in [docs/development-plan.md](docs/development-plan.md). The current cloud release constant is `1.0.20`.

## Running Locally

For hosting choices, Windows/macOS/Linux prerequisites, current support limits,
and the conversion journey, start with the [platform setup guide](docs/platform-setup-guide.md).

For local setup, use [docs/local-cloud-quickstart.md](docs/local-cloud-quickstart.md).

To self-host the Docker Compose stack on your own hardware, start with [docs/self-hosted-runbook.md](docs/self-hosted-runbook.md).

For a fresh, digest-pinned native Linux experiment without a source build, use
the [preview download instructions](https://transcendentsoftware-jd.github.io/JiboExperiments/downloads.html).
The owner-selected experimental preview is `runtime-preview-38165346370`.
Portable is the default; AVX2 requires explicit selection and actual daemon-host
CPU checks. Both passed synthetic install/speech/restore testing, not stable
or physical-robot certification. [Evidence and remaining gates](docs/guarded-source-preview-publication-20261011.md).

For device bootstrap and OOBE QR setup, use:

- [docs/device-bootstrap.md](docs/device-bootstrap.md)
- [docs/wifi-qr-oobe.md](docs/wifi-qr-oobe.md)

It covers:

- the current `.NET` OpenJibo cloud
- the legacy Node protocol oracle
- the Playground direct-to-Jibo ASR/TTS demo

## Roadmap

The long-range plan is summarized in [docs/roadmap.md](docs/roadmap.md). In short:

1. Working hosted cloud.
2. OTA-assisted recovery and updates.
3. Open Jibo OS / `open-jibo` mode conversion.
4. Tiered brain and CoffeeBreak orchestration.
5. Broader ecosystem expansion.

## Current Architecture

The repo now has three distinct lanes:

- `src/Jibo.Cloud/node`
  Protocol oracle, discovery server, fixture source, and rapid reverse-engineering lab.
- `src/Jibo.Cloud/dotnet`
  Production-oriented hosted implementation intended for Azure deployment and long-term maintenance.
- `src/Jibo.Runtime.Abstractions`
  The seam between robot/cloud traffic and higher-level runtime and capability logic.

The core shape is:

```text
Jibo device -> OpenJibo cloud -> normalized runtime contracts -> capabilities and planning
```

## First Supported Device Path

The first supported recovery path is enthusiast-friendly, not zero-touch:

```text
QR Wi-Fi -> inject OpenJibo region config -> set robot region ->
RCM/device patch for TLS and host acceptance -> OpenJibo cloud on Azure
```

That path is documented in [docs/device-bootstrap.md](docs/device-bootstrap.md).

## Design Principles

- Preserve the original skills and visual design.
- Build the hosted cloud before making OTA the default recovery path.
- Keep every migration reversible whenever possible.
- Prefer source-backed slices over speculative rewrites.
- Let Jibo remain the face of the experience, even when higher-level orchestration sits behind him.

## Repo Map

```text
OpenJibo/
  docs/
    architecture/
    cloud-deployment-topology-plan.md
    roadmap.md
    development-plan.md
    device-bootstrap.md
    feature-backlog.md
    open-jibo-mode-conversion-plan.md
    public-site-plan.md
    regression-test-plan.md
    release-1.0.19-plan.md
    release-1.0.20-plan.md
    support-tiers.md
    system-diagram-alignment.md

  scripts/bootstrap/
    Discover-JiboHosts.ps1
    Generate-JiboDnsOverrides.ps1
    Test-OpenJiboRouting.ps1

  src/
  Jibo.Cloud/
      node/
      dotnet/
    Jibo.Runtime.Abstractions/
    Jibo WiFi QR Generator/   ← standalone HTML tool for generating OOBE QR codes
    Playground/
    OpenJibo.Site/
```

## Living Docs

Use these when you want the active technical truth:

- [Development plan](docs/development-plan.md)
- [Cloud deployment and topology plan](docs/cloud-deployment-topology-plan.md)
- [Storage trust and consensus plan](docs/storage-trust-consensus-plan.md)
- [Architecture decisions](docs/architecture/README.md)
- [Runtime usage outbox boundary](docs/architecture/runtime-usage-outbox.md)
- [Local cloud quickstart](docs/local-cloud-quickstart.md)
- [Device bootstrap](docs/device-bootstrap.md)
- [WiFi QR OOBE flow](docs/wifi-qr-oobe.md)
- [OOBE / wipe / BE audit](docs/oobe-wipe-be-flow-audit.md)
- [Feature backlog](docs/feature-backlog.md)
- [Open Jibo mode conversion plan](docs/open-jibo-mode-conversion-plan.md)
- [Release 1.0.19 closeout](docs/release-1.0.19-plan.md)
- [Release 1.0.20 plan](docs/release-1.0.20-plan.md)
- [Transcendent Software OpenJibo Cloud managed service](https://cloud.openjibo.com)
- [Support tiers](docs/support-tiers.md)
- [System diagram alignment](docs/system-diagram-alignment.md)
- [Public site plan](docs/public-site-plan.md)

If you only read one document for the long view, make it [docs/roadmap.md](docs/roadmap.md).
