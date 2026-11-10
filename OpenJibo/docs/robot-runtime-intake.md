# Read-only runtime intake on a stock robot

`scripts/bootstrap/inspect-robot-runtime.js` uses ES5 syntax and fixed file paths.
It reports the system-manager package version, configuration-file presence and
only the `api`/`open-jibo` hostname candidates and notification suffix. It stats
the credentials file for presence but never opens it. There are no network calls,
writes, partition mounts, service restarts or credential derivations in the tool.

On an already authorized SSH connection, a POSIX host shell can stream the source
into the robot's existing Node process without installing a file:

```sh
ssh root@ROBOT 'node -e "$(cat); process.stdout.write(JSON.stringify(module.exports.inspect(require(\"fs\"))) + \"\\n\")"' < scripts/bootstrap/inspect-robot-runtime.js
```

Keep SSH host-key verification enabled and use the operator's approved login
method. This command is intended for a host POSIX shell; a Windows operator can
use its existing authorized SSH tooling to pass the same source as a quoted
`node -e` argument. Do not substitute `node -`: some stock Node versions reject
that invocation. The script also runs normally as `node inspect-robot-runtime.js`
if a separately authorized copy is already present. Additional arguments are
refused. The host-only first-contact scan and OTA planner use modern Node features
and should not be assumed executable by the robot's bundled interpreter.

Treat output as private operator inventory. The reported hostnames are configured
candidates; neither their presence nor the old `xxx_override` section proves
which route the running process uses. The tool intentionally omits that override.
A system-manager package version is a component version, not verified JiboOS
release identity. Unknown or malformed fields stay unknown. A present credential
file is not proof of valid credentials, unique robot identity or a functional OOBE
installation.

Every report keeps firmware, active routing, credential identity and conversion
readiness unverified. Missing OOBE files do not manufacture a default region or
credentials. Establish the device-specific firmware/recovery baseline and current
effective routing separately before conversion. This inventory does not establish
ownership, authorize firmware distribution or enable a managed paid profile.

The tests use only synthetic files, including malformed, oversized and growing
inputs, credential-bearing URL values and a credentials file that must never be
opened:

```sh
node --test tests/Jibo.Cloud.JavaScript.Tests/robot-runtime-inventory.test.mjs
```
