# Portable preview on Windows Docker Desktop

The fresh preview launcher supports Windows x86_64 with a local Docker Desktop
Linux daemon. It uses the signed starter's existing PowerShell initializer;
Bash and OpenSSL are not Windows launcher prerequisites. Native Linux launch
behavior remains available. AVX2 retains its confirmed native Linux requirement;
select portable on Windows.

The runtime remains `runtime-preview-41768412801`, source
`cb33f0c46aacfa062e3fb351196f6e3be2641d8a`. This launcher update does not rebuild
or replace its image or signed ZIP. Portable image:

```text
ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:e624e4b665c671270ce9ce1f7368799c61c6111e2156fd9049fa9845c6a81d5a
```

## Prepare and check

Use the current trusted checkout with Python 3.9+, a GitHub CLI supporting
attestation verification, PowerShell and Docker Desktop in Linux-container mode.
The daemon needs at least 4 GiB RAM, the Windows host 1 GiB available RAM, and the
prepared folder's filesystem 3 GiB free. These are current prerequisite checks,
not a capacity or performance certification. Only canonical local named-pipe
Docker contexts are accepted; a different machine's daemon cannot use this
host's installation paths safely.

From `OpenJibo`, choose a fresh destination and an unused project/loopback port:

```powershell
py -3 -I -B scripts/cloud/prepare-published-starter.py --channel preview --version runtime-preview-41768412801 --cpu-profile portable --destination C:\Jibo\transfer-preview
py -3 -I -B scripts/cloud/launch-published-starter.py --directory C:\Jibo\transfer-preview --project openjibo-transfer-preview --port 8099
```

Preparation verifies the exact archive and source-bound signature before
extraction. The launch command above is read-only: it checks files, host/daemon
resources, project conflicts and the port without creating `.env` or starting
Docker. Curated prerequisite failures explain missing resources; arbitrary
failure text remains redacted.

Add `--start` only after that check passes. The launcher rechecks immediately,
generates the private environment with the verified PowerShell script, pins the
Compose image, disables Azure Speech for this local test profile, binds the API
to loopback and waits for health. PowerShell's execution-policy option applies
to that child process; machine-wide policy is unchanged. Windows file protection
uses the installation directory's existing ACLs; the launcher does not configure
ACLs or encrypt backups. Keep the directory private.

The fresh-only launcher refuses an existing environment or project resources.
Preserve them after a failed attempt. Use the original project/environment/files
for normal stop/restart and recovery; do not delete keys or volumes to make a
fresh-only check pass. Follow the [recovery guide](self-hosted-recovery.md).

## Recorded Windows acceptance

The exact portable image and signed ZIP passed 38 parent checks on Windows with
Docker Desktop's Linux amd64 daemon. Each installation used fresh named volumes
and loopback port 19997. Tests included three synthetic robot identities and
primary selection, 20 real HTTP/WebSocket account/ownership scenarios, actual
API/PostgreSQL container replacement, full backup verification, separate-volume
restore and six retained-state checks after restart and again after restore.

The shipped-library audit compared canonical IDs, creation timestamps, loop
associations, primary selection, ownership/legacy generations, all stable
encrypted fields and the current owner's original command secret with the
backup. Three encrypted private record generations remained present while only
the current generation appeared via the API. Expected last-seen metadata advanced
after restored Home Assistant reconnect. Binary API-volume bytes and a personal
memory fixture were restored too.

Seven recorded synthetic local-Whisper turns passed with Azure Speech disabled:
three initially, one after restart and three after restore. Node 24's native
WebSocket client used a test EventEmitter adapter and the supported hub-token
URL path; the Linux tests separately exercised the header adapter. These are
fixture observations, not a microphone, physical playback or performance SLA.
No real calendar feed or external Home Assistant server was contacted.

The original Linux-only guard and a pre-mutation harness report-key mistake were
retained before the Windows feature run. Later read-only checks also retained a
low-disk refusal and a transient Docker command timeout. Only rebuildable output
from explicitly owned worktrees was removed to recover disk space; keys, backups
and Docker volumes were retained. Final source checks passed without initializing
an environment. This is not a claim that every harness attempt passed first time.

All new Windows services are stopped with their private environment, volumes
and backups retained. No physical Jibo or production cloud was changed. AVX2 on
Windows, macOS/ARM/emulation, physical OOBE/repeat deployment and general
version-pair upgrade compatibility remain separate gates. The units intended
for physical testing are still awaiting delivery; preserve one as a reference.
