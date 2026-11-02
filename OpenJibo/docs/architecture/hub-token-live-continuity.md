# Credential-bound Hub socket continuity

The credential-bound Hub-token foundation revalidates durable token and account
state at each handshake. Active bound Hub sockets now also run a continuity
monitor independently of receive, idle-turn and reply processing.

## Boundary

At the handshake, the coordinator captures the original validated
`HubTokenCredentialBinding`. A bound connection revalidates immediately and then
on a five-second timer. Each asynchronous validation has a five-second timeout
and receives cancellation. One validation runs at a time per connection.

The PostgreSQL implementation reads the unexpired, unrevoked token row and the
current account directly. It requires token kind `hub`, the exact original
binding (including signature time and operation-authenticated classification),
and the current account access-key fingerprint. It does not consult or hydrate
the process-local session cache. Removing binding metadata cannot downgrade an
already-bound connection to unbound compatibility mode.

An invalid result, exception or timeout cancels the connection's ongoing work
and aborts the socket. Abort can safely interrupt an in-flight send/receive;
the monitor does not race a WebSocket close handshake against reply sends or
wait for an unresponsive peer. The existing coordinator finally block removes
presence/notification registrations and releases the session only when that
connection still owns it. A credential failure does not invoke the premature
audio-loop completion path.

The validation window is the next timer check plus the bounded provider wait;
this is not hard real-time or a database check on every frame. Cancellation of
application work remains cooperative, while socket abort prevents further
delivery on that connection. Connection cancellation stops and joins the
monitor. Provider exception text is not returned or logged by the monitor;
the close telemetry uses only `credential-invalid` or
`credential-validation-unavailable`.

Unbound compatibility tokens, explicit tokenless isolated-hosting mode, robot
API sockets and Home Assistant sockets do not start this monitor. The patch
does not turn stock traffic into an ownership decision or require a new robot
credential scheme.

## What this does not establish

This is continuity with an exportable legacy account credential, not physical
device identity, human co-presence or managed membership. Secret-only rotation
under the same access-key ID is still not detectable from its fingerprint;
the rotation workflow must explicitly revoke issued Hub tokens.

The change does not provide one-live-socket-per-token fencing across replicas,
consume SigV4 replays synchronously, fetch managed pairing descriptors, call the
disabled attestation issuer, assign ownership or enforce managed entitlements.
Those steps still need their own transaction and protocol integration. The
existing replay observation store, Hub binding and issuer remain reusable
foundations rather than proof that pairing is enabled.

## Validation and release

Targeted coordinator tests hold receive open and change/remove the original
binding for listen and proactive sockets. They check abort, no inbound/reply
processing, session release and the bounded close reason. Monitor tests cover
periodic revocation, backend exceptions, an unresponsive validator and normal
cancellation. An unbound compatibility test verifies that no monitor starts.

The PostgreSQL integration test uses two independent store instances, first
populates a positive cache, then changes durable revocation, expiry or metadata
and rotates the account access-key ID. Both stores must reject the original
binding. CI supplies its isolated PostgreSQL service; local runs without that
service do not count as database acceptance.

No deployment or physical-device test accompanies source implementation. Before
production rollout, measure the additional token/account reads, prove a bound
socket and reconnect close after a staging revocation, verify unavailable-store
behavior and ordinary stock/isolated compatibility, and retain an image rollback.
Do not reopen a consumed registry test window or enable managed pairing through
this change.
