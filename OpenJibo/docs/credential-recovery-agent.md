# Local credential recovery agent candidate

This Linux/Node 6.9.2-compatible candidate composes the local credential transaction
with a detached recovery supervisor and a source-pinned consumer-control profile.
It has no installer, default credential directory, boot hook, cloud endpoint or
bundled real-device service-control profile. Qualification uses disposable files
and a resident fixture consumer only. It does not grant ownership or service access.

## Bound local package

A separately reviewed installer would prepare a private
`.openjibo-delivery-<32 hex operation ID>` directory directly inside the explicit
credential directory. `driver.js` is the agent source; `transaction.js` is the
reviewed transaction source; `consumer-profile.js` is a separately qualified
consumer adapter. Files must be regular, single-link, owned by the executing
UID/GID, mode 0600; the helper directory must be mode 0700 without path aliases.
The parent must retain its reviewed ownership and cannot be group/world writable.
The stock transaction profile additionally requires UID/GID 0 and preserves the
original 0666 file mode on restoration. The replacement and private backups use
0600. No real-device installation is authorized or performed by this candidate.

`agent.json` is compact canonical JSON with one trailing newline and exactly:

```
schema: openjibo.credential-agent-config.v1
binding: immutable operation/unit/source/credential hashes and scenario
credentialDirectory: explicit absolute parent directory
transactionProfile: private-0600 | stock-root-0666-to-0600
watchdogMs: integer 500..30000
actionTimeoutMs: integer 100..1000
```

The binding matches the framed `openjibo.owned-delivery-rpc.v1` contract. Its opaque
identity fields and association/recovery hashes reference evidence; they do not
verify physical possession, ownership or restore readiness. The caller must enforce
current authority separately. Configuration and all three source hashes are checked
again during the attempt. Unexpected changes refuse rather than replacing evidence.
Production profile qualification must also verify mount interpretation, extended
metadata/security labels, real unit association and firmware compatibility.

## Phases and recovery

The driver accepts one bounded canonical JSON request on stdin and emits only a
bound receipt or a fixed refusal. New credential bytes use stdin only during
`prepare`; passwords are outside the agent. Unknown fields, duplicate JSON names,
changed bindings, nonces without the expected format and expired requests refuse.
No shell-selected commands or remote network endpoints are accepted.

`inspect` invokes the reviewed profile without creating an attempt. `prepare`
reserves a one-use attempt and robot-directory claim, then starts a detached
supervisor before staging the transaction. A private Unix socket connects later
driver invocations to that process. A startup failure preserves the reservation;
it cannot silently replay preparation. `arm` acknowledges the running supervisor
and its bounded monotonic deadline. Activation requires that live arm, quiesces the
qualified consumer, rechecks request expiry, and installs once. Scenario 1 permits
one controlled reload; scenario 0 refuses it. File and reload acknowledgements are
not independent consumer acceptance evidence.

At the watchdog deadline, or after a failed forward phase, the supervisor:

1. Durably fences all later forward phases and stops/drains its profile worker.
2. Requires the profile to fence and settle downstream consumer work. Killing a
   local process alone does not prove a previously admitted reload is settled.
3. Restores exact original bytes/mode through the transaction, preserving unknown
   edits, unsafe metadata and stale writer locks on refusal.
4. Reloads and independently observes the original consumer configuration. A
   repaired file without that observation remains `recovery-unverified`.

The watchdog survives controller exit and a killed in-flight driver. It is not a
boot/power-loss mechanism, hard real-time deadline, or protection against killing
the supervisor itself. Supervisor death prevents further admission; it can leave
the replacement installed with private original bytes retained. Stale sockets,
claims and transaction locks are not broken or restarted automatically. A reviewed
operator recovery path remains necessary for those failures and filesystem faults.

Repeated restoration after success reads back the original consumer rather than
reloading it again. At deadline closeout the supervisor verifies the original file
and consumer again, records closeout and releases only its exact unit claim. The
old attempt stays fenced and its journals remain. A fresh operation may then start;
an old cleanup request cannot overwrite that later operation. Failed closeout
retains the claim and requires investigation. No blanket recursive cleanup exists.

## Consumer profile contract

`consumer-profile.js` exports `run(action, request, credentialDirectory)` returning
a promise of `true` only after verifying the requested action. The reviewed actions
are `inspect`, `quiesce`, `reloadNew`, `fence`, `reloadOriginal`, `observeNew` and
`observeOriginal`. A worker has a short deadline and bounded output; raw profile
errors do not reach receipts. The profile must independently enforce operation
fencing, expiry and downstream settlement. It cannot treat worker death or an RPC
acknowledgement as evidence that the consumer loaded a credential.

The fixture profile deliberately accepts only temporary `cra-*` directories. Its
resident consumer retains the loaded credential hash in memory and changes it
only on a controlled reload. A delayed new reload checks its operation fence before
applying. This profile executes no system service commands and cannot be used on
a live credential directory. Real supervisor APIs, consumer dependencies, reload
observations and interruption/recovery timing remain separately unqualified.

## Acceptance evidence

CI requires 27 groups without skips on Node 6.9.2 and Node 24, both as a normal
user with private mode 0600 and as root with stock 0666-to-0600-to-0666. Groups
exercise resident-consumer readback, controller exit/death, delayed reload fencing,
lost acknowledgement, replay denial, expiry during quiesce, source/config drift,
aliases/unsafe metadata, unknown edits, stale transaction locks, failed new/original
reload, failed downstream fencing, missing original observation, supervisor death,
unit serialization and repeat-operation closeout. Windows mutation refuses.
Source hashes and both runtime receipts are retained with each CI candidate.
These fixtures do not establish physical reload, full-image restoration, firmware
redistribution, credential commissioning or production readiness.

## Cleanup readback after verified closeout

When the watchdog has restored, observed, closed and exited, a returning controller
may request only fresh `fence`/`restore` readback against that terminal attempt.
The driver validates all five source-bound terminal records and the original
file, then acquires a temporary exclusive unit claim. It fences the old consumer
operation and observes the original again; it does not restart the watchdog,
rewrite credentials, reload the consumer or permit any forward phase. Only a
successful fresh readback releases that temporary claim. Ambiguity retains it.

A changed baseline is refused before reserving the unit, and an active later
attempt's claim is never stolen. An old cleanup cannot overwrite that later
operation. Missing terminal records, supervisor death before closeout, or stale
claims retain the manual recovery boundary described above.
