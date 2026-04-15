# Device Bootstrap Path

## Supported First Path

The first supported OpenJibo recovery path is:

```text
QR Wi-Fi -> inject OpenJibo region config -> set robot region ->
RCM/device patch -> Azure-hosted OpenJibo cloud
```

This is the path we can document, repeat, and improve.

## Why This Path Comes First

- it matches the region-driven configuration seams observed on the robot
- it keeps the hosted cloud work grounded in real device traffic
- it avoids blocking the entire revival on OTA before cloud compatibility exists

## Bootstrap Checklist

1. Connect the robot to a controlled Wi-Fi network.
2. Add an OpenJibo region entry to `/etc/jibo-jetstream-service.json`.
3. Set the robot `region` field in `/var/jibo/credentials.json` to the OpenJibo region.
4. Gain RCM/device access for targeted TLS or host validation changes.
5. Verify robot startup, token flow, socket flow, and first-turn behavior.

## Region-Driven Configuration

Current findings suggest the preferred OpenJibo bootstrap path is to inject a new region configuration rather than override every hostname manually.

Confirmed paths:

- `/etc/jibo-jetstream-service.json`
  Add an OpenJibo region definition that points Jibo to our cloud.
- `/var/jibo/credentials.json`
  Set the robot `region` field to the injected OpenJibo region.

Observed additional region-related files worth documenting and auditing:

- `/etc/jibo-ssm/*.json`
- `/skills/jibo/Jibo/Skills/@be/be/node_modules/language-subtag-registry/data/json/registry.json`
- `/skills/jibo/Jibo/Skills/oobe-config/config.json`

These should be treated as configuration discovery targets, not yet as the authoritative complete list.

## Required Hosts

The currently relevant public hostnames for the OpenJibo cloud path are:

- `api.jibo.com`
- `api-socket.jibo.com`
- `neo-hub.jibo.com`

## Scripted Helpers

Bootstrap helper scripts live in [scripts/bootstrap](/OpenJibo/scripts/bootstrap):

- `Discover-JiboHosts.ps1`
- `Generate-JiboDnsOverrides.ps1`
- `Test-OpenJiboRouting.ps1`

These are intentionally conservative helpers for discovery and verification, not destructive patch tools. They remain useful for controlled-network testing, even though the preferred long-term device path is region injection.

## TLS And Runtime Patching

Patching requirements will vary by device version and by where certificate validation is enforced.

Near-term guidance:

- record each patch location by software version
- prefer small, repeatable changes over ad hoc edits
- keep a versioned host inventory and patch checklist
- keep a versioned region-config checklist
- do not describe OTA as the primary bootstrap method until the hosted cloud is stable

## Smoke Test Goals

The first real-device smoke test should confirm:

- robot startup reaches the hosted cloud
- token issuance succeeds
- required sockets connect
- the robot can complete one simple turn
- update metadata calls do not break startup
