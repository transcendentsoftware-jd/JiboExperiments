# Device Bootstrap Path

## Supported First Path

The first supported OpenJibo recovery path is:

```text
QR Wi-Fi -> controlled router/DNS -> redirect Jibo hosts ->
RCM/device patch -> Azure-hosted OpenJibo cloud
```

This is the path we can document, repeat, and improve.

## Why This Path Comes First

- it matches what the current Node prototype already requires
- it keeps the hosted cloud work grounded in real device traffic
- it avoids blocking the entire revival on OTA before cloud compatibility exists

## Bootstrap Checklist

1. Connect the robot to a controlled Wi-Fi network.
2. Redirect legacy cloud hostnames to the OpenJibo environment.
3. Prevent fallback DNS from bypassing the controlled resolver.
4. Gain RCM/device access for targeted TLS or host validation changes.
5. Verify robot startup, token flow, socket flow, and first-turn behavior.

## Required Host Routing

At minimum, watch and validate:

- `api.jibo.com`
- `api-socket.jibo.com`
- `neo-hub.jibo.com`
- `neohub.jibo.com`

## Scripted Helpers

Bootstrap helper scripts live in [scripts/bootstrap](C:/Projects/JiboExperiments/OpenJibo/scripts/bootstrap):

- `Discover-JiboHosts.ps1`
- `Generate-JiboDnsOverrides.ps1`
- `Test-OpenJiboRouting.ps1`

These are intentionally conservative helpers for discovery and verification, not destructive patch tools.

## TLS And Runtime Patching

Patching requirements will vary by device version and by where certificate validation is enforced.

Near-term guidance:

- record each patch location by software version
- prefer small, repeatable changes over ad hoc edits
- keep a versioned host inventory and patch checklist
- do not describe OTA as the primary bootstrap method until the hosted cloud is stable

## Smoke Test Goals

The first real-device smoke test should confirm:

- robot startup reaches the hosted cloud
- token issuance succeeds
- required sockets connect
- the robot can complete one simple turn
- update metadata calls do not break startup
