# Isolated registry image acceptance

`scripts/cloud/check-registry-acceptance-image.py` exercises the actual built
runtime image with fresh, synthetic credentials and disposable PostgreSQL.
It requires an existing local image and exact checked-out source revision.
It never changes an existing Compose stack, contacts a robot or imports a backup.

The controller creates one UUID-owned internal Docker network, a PostgreSQL
container with temporary storage and three successive API processes. Only the
API has a published port, bound to `127.0.0.1`. The database creates two fresh
test databases and a non-superuser runtime role; a separate migration process
uses the fixture administrator. No real database password or provider key is
supplied. Telemetry exports and speech providers are disabled.

Acceptance runs three phases:

1. Both credentials obtain Hub and notification tokens and reconnect on Listen,
   Proactive and notification sockets. Unknown credentials, a wrong secret and
   an unsigned token request are refused.
2. Stop and remove the first API process, then start a B-only inventory against
   the same database. A's signature and old tokens fail; B's old tokens continue
   working on all three channels.
3. Stop and remove that API process, then start a new A secret plus unchanged B.
   Old A signatures/tokens fail. Fresh A authentication obtains new tokens and
   reconnects, while B's original tokens still work.

The client sends exactly twelve token POSTs and twenty-one fresh WebSocket
handshakes, retaining thirty-three status-only results. Token POSTs and socket
handshakes are not retried. Readiness GET polling has a bounded deadline.
No audio or turn request is sent. Bearers and generated secrets remain private
to the process/temporary fixture files and are excluded from the proof.

The unchanged PostgreSQL process and accepted original B bearers distinguish
credential removal from accidental state loss. The controller checks ownership
before stopping/removing resources and refuses cleanup of an unknown container.
It removes its network and containers without touching other volumes or stacks.

CI runs this acceptance on the same image used for ordinary stack smoke tests
and retains hashes of exact sources, the image identity, all thirty-three
outcomes and the cleanup result. Failure evidence reports incomplete acceptance;
it must not be promoted as success. The HTTP-only local image controller is
loopback-only. A separate trusted hosted controller may supply one exact HTTPS
test hostname; hosted resource publication and cleanup belong to that operator's
deployment repository and authorization scope.

This exercises the registry profile. It does not establish a paid entitlement,
physical possession, credential delivery, enrollment, firmware compatibility or
robot recovery. See [trusted credentials](trusted-protocol-credentials.md) for
the immutable snapshot and controlled-restart boundary.
