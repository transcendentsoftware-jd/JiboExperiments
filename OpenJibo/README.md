# OpenJibo

## Summary

OpenJibo is the working revival track for Jibo.

The near-term plan is intentionally concrete:

1. Build a stable replacement cloud on Azure.
2. Use the existing Node prototype as the protocol oracle and capture harness.
3. Port the hosted implementation to .NET as a modular monolith.
4. Bring real robots online first through RCM plus controlled DNS/TLS patching.
5. Use OTA later to reduce setup friction once the hosted cloud is proven.

This keeps the project grounded in what is already working while moving toward a maintainable hosted platform.

## Current Truth

The repo now has three distinct lanes:

- `src/Jibo.Cloud/node`
  The discovery server. This is the best source of observed protocol behavior today.
- `src/Jibo.Cloud/dotnet`
  The long-term hosted implementation. This is where the stable cloud is being built.
- `src/Jibo.Runtime.Abstractions`
  The normalized runtime seam between robot/cloud traffic and modern conversation logic.

The key architectural idea is:

```text
Jibo device -> OpenJibo cloud -> normalized runtime contracts -> capabilities and planning
```

## First Supported Device Path

The first supported recovery path is enthusiast-friendly, not zero-touch:

```text
QR Wi-Fi -> controlled router/DNS -> redirect legacy Jibo hosts ->
RCM/device patch for TLS and host acceptance -> OpenJibo cloud on Azure
```

That path is documented in [docs/device-bootstrap.md](C:/Projects/JiboExperiments/OpenJibo/docs/device-bootstrap.md).

## Repo Map

```text
OpenJibo/
  docs/
    device-bootstrap.md
    protocol-inventory.md
    public-site-plan.md
    support-tiers.md

  scripts/bootstrap/
    Discover-JiboHosts.ps1
    Generate-JiboDnsOverrides.ps1
    Test-OpenJiboRouting.ps1

  src/
    Jibo.Cloud/
      node/
      dotnet/
    Jibo.Runtime.Abstractions/
    Playground/
    OpenJibo.Site/
```

## Decisions Locked In

- The first milestone is `core revive`, not full protocol parity.
- Azure SQL is the relational system of record for the hosted cloud.
- Billing and donations are future-compatible concerns, not phase-one delivery requirements.
- OTA is a phase-two simplification strategy, not the initial dependency.

## Near-Term Work

- port required endpoint and WebSocket behavior from Node to .NET
- keep protocol captures and replay fixtures current
- harden device bootstrap documentation and scripts
- stand up the initial `openjibo.com` information site

## Important Docs

- [Cloud overview](/src/Jibo.Cloud/README.md)
- [Protocol inventory](/docs/protocol-inventory.md)
- [Support tiers](/docs/support-tiers.md)
- [Device bootstrap path](/docs/device-bootstrap.md)
- [Public site plan](/docs/public-site-plan.md)