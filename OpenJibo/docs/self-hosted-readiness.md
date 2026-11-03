# Self-hosted setup observations and conversion target

`prepare-self-hosted-readiness.py` joins the existing host prerequisite checker
with a running runtime's redacted configuration profile. It exports a local HTML
page, a JSON observation record and, when the checked conditions pass, a pinned
`target.json` compatible with the AutoMod offline conversion review.

This is a read-only observation/export command. It does not initialize `.env`,
start containers, query or migrate a database, enroll an owner, contact a robot,
or apply conversion changes. It needs Python 3.9+ and the existing Docker/Compose
host path. Continue to use the [self-hosted runbook](self-hosted-runbook.md) for
installation, secrets, persistence and recovery.

## Run

Use a runtime built from source containing `GET /health/self-hosted`. Older
published previews without that route return an attention report, not success.
No existing deployment or image is upgraded by this command.

Windows, from `OpenJibo`:

```powershell
py -3 scripts/cloud/prepare-self-hosted-readiness.py `
  --api-origin http://192.168.50.20:8080 --hub-origin http://hub.local:8080 `
  --allow-isolated-http --output C:\review\new-server-observations
```

macOS/Linux:

```bash
python3 scripts/cloud/prepare-self-hosted-readiness.py \
  --api-origin http://192.168.50.20:8080 --hub-origin http://hub.local:8080 \
  --allow-isolated-http --output /review/new-server-observations
```

Replace example origins with your selected server addresses. HTTP requires the
explicit isolated acknowledgement and a private/LAN or `.local` address; HTTPS
uses normal host certificate validation. URLs containing credentials, arbitrary
paths, queries, fragments or unsupported host syntax are refused. Loopback is
not a robot-facing conversion target.

For local setup inspection before exposing the API to the selected LAN, add
`--probe-origin http://127.0.0.1:8080`. This observes local configuration but
**withholds target export** because the probed API differs from the selected
robot-facing origin. Probe the actual selected API origin before exporting its
target. The Hub origin is an explicit selection, not a tested socket endpoint.

The output parent must exist and the output directory must be new. Exit 0 means
the checked host/configuration/origin conditions passed and a target was exported.
Exit 2 can mean either a complete attention report or an input/export refusal;
stdout's `reportExported` and a terminal `readiness.json` distinguish them. Without
a completed report and matching file hashes, partial output is incomplete.

## What is observed

The host checker makes its existing read-only Docker/Compose queries on the
selected context. A separate supervised Python child makes one unauthenticated
GET to `/health/self-hosted`; no credentials, cookies, proxy configuration or
redirects are used. Response bytes are limited to 8 KiB. The parent kills and
reaps a child exceeding `--timeout` (default 5 seconds, 1–30). The host checks
have their own per-command timeout. This is not one shared deadline for the whole
packet. Raw response bodies, CLI diagnostics, paths, environment values and
exception messages are omitted. Selected origins intentionally remain visible.

The runtime route is available only in explicit `self-hosted-isolated` mode and
returns a fixed configuration schema. State/personal-memory backend names are
allowlisted; owner, robot, connection string, credential, inventory and replica
data are absent. It is a cheap configuration snapshot with no state-store query.
All connectivity/robot/apply claims remain false. PostgreSql must be explicitly
configured for both stores for this Compose-starter export path; a configured
backend does not establish that its database is reachable or its schema is valid.

An exported `target.json` uses `openjibo.conversion-target.v1`, `mode: isolated`,
`providerId: self-hosted`, and the selected API origin. The report records its raw
and canonical SHA-256 plus a file index. Use that file as the target input when
preparing the [AutoMod review](https://github.com/Jibo-Revival-Group/JiboAutoMod).
The report's selected Hub origin can inform the separate endpoint manifest;
credential region must come from appropriate robot/storage evidence, not this
server. No other conversion input is manufactured by this export.

The report and HTML distinguish observations from installation acceptance.
`engineToRuntimeAssociationVerified`, `runtimeImageDigestVerified`, database
connectivity/migrations, restart persistence/restore, robot networking/protocol,
OOBE/repeat deployment, `installCertified` and `readyForApply` remain false.
Runtime JSON is a self-report, not authenticated provenance. Host TLS acceptance
does not verify a robot trust store. No ARM64/emulation support is added by export.

## Multiple robots and OOBE

The tokenless single-robot compatibility setting admits one client address at a
time. It is not multi-robot enrollment. Use issued tokens or separately isolated
instances when rehearsing multiple robots; this tool never enables that setting.
The [three-robot test plan](oobe-and-repeat-deployment-test-plan.md) separates the
existing robot, two incoming OOBE units, first deployment, repeat server startup,
and later separately scoped reset/recovery tests. A fresh OOBE robot may have no
credential region; the current credentialed conversion review must not infer one.

CI exports synthetic packets on all three OSes and checks the actual disposable
runtime route before and after stack restart. That verifies the wire contract
and process availability. It does not certify real robot deployment, data survival,
host installation, recovery or a newly published runtime image.
