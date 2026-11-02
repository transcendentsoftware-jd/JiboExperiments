# Opt-in Hub channel lease lifecycle

The WebSocket coordinator now has a default-disabled transport lease profile over
the [durable channel lease primitive](hub-channel-lease-foundation.md). It is not
atomic fencing of application state, external actions or managed ownership.
No deployment or configuration change accompanies this implementation.

## Admission and lifetime

With `OpenJibo:Security:HubChannelLeases:Enabled` omitted/false, existing stock,
unbound, tokenless isolated, API and Home Assistant behavior remains unchanged.
When enabled, Hub listen/proactive handshakes require a valid credential-bound
Hub token. Unbound/tokenless Hub requests are rejected with 401; invalid enabled
configuration or unavailable/uncertain acquisition returns 503 before accepting
the socket. API and Home Assistant sockets do not acquire channel leases.
Before acquisition, the original token/account binding is authoritatively checked
with a two-second async bound; invalid binding returns 401 and unavailable checks
return 503. The active continuity monitor continues to revalidate after acceptance.
Credential-bound classification is not operation authentication or robot ownership.

The profile requires an explicit dedicated `ConnectionString` and `HmacKey` under
the same section. The key is canonical unpadded base64url containing 32–64 bytes;
the connection has no fallback to cloud-state, observer or replay-admission
credentials. Non-loopback connections require certificate and hostname
verification. Invalid enabled configuration never falls back to disabled mode.
Configuration binding errors may prevent startup.

The policy derives a SHA-256 HMAC over UTF-8
`openjibo-hub-channel-lease-v1\0` plus the exact bearer, using the dedicated key.
Only that opaque digest, channel and fresh random connection-owner UUID reach
the lease store. Both channels use the same digest with different channel slots.
Every participating replica must use the same stable key. There is no online
overlap/key-rotation mechanism; changing keys can split the fencing namespace.

Acquisition, checks, renewal and release have two-second async wait bounds,
forward cancellation and never retry. Exceptions and lost responses are
uncertain results, never authority to continue. Independent renewal runs
immediately and then every five seconds, including while receive/send or turn
processing is blocked. False, invalid-generation, unavailable or expired results
cancel connection work and abort the socket; the monitor is stopped and joined
during teardown. The credential continuity monitor remains independently active.

The database TTL is 30 seconds. The handle uses a conservative 25-second
monotonic deadline measured from the start of the successful acquire/renew
request, not from receipt of its response or local UTC. A renewal response that
arrives after the old local deadline cannot revive the handle. Wall-clock jumps
cannot extend local authority. A process that resumes after its deadline fails
at the next lease guard; this is not hard real-time scheduling.

Guards check the exact owner/generation before inbound processing and each idle
watchdog iteration. Every final or ambient progress reply is checked again after
any reply delay and before sending. A failed guard cancels/aborts immediately.
There remains a race between a successful check and later work or send. Socket
abort stops delivery, while cancellation of application work is cooperative.
Provider exception details, raw tokens and keys are not used in close reasons.
Telemetry records only `lease-invalid`, `lease-expired` or `lease-unavailable`.

## Shared-session teardown

The two channels still share the token-keyed session and turn state. The leased
profile tracks local connection ownership separately per channel. An older local
socket cannot clear a successor's channel marker. Proactive teardown does not
release listen audio; listen teardown releases local buffered audio only while
that connection still owns its local channel marker.

Once the last local channel marker is removed, teardown calls `CloseSession`
under the local session gate. In both existing stores this removes only the
process-local active registry entry. It does not delete a database session row,
revoke a durable token or mutate another replica's session/lease state. An older
local connection cannot remove its successor's marker or entry. Other local
channels keep the shared entry alive. Leased teardown still skips premature-loop
completion, which can mutate shared turn state after a lost lease.

This is not a repair of existing stock Word of the Day behavior.
Exact-handle lease release is attempted once with a
bounded wait; if unavailable, the lease expires. Stale release cannot erase a
successor's durable generation.

## Activation gates and remaining work

Do not enable on the serving fleet as an ordinary rolling configuration change.
Drain existing sockets/old-profile replicas, coordinate the key and enabled
policy on all participating nodes, provision a reviewed restricted function owner
and EXECUTE-only identity, and preserve generation tombstones. Prove replacement,
expiry, revocation, database loss, delayed replies and reconnects in staging with
an image/configuration rollback. No new role or grant is provisioned automatically.

The current guards can perform one database check per 50-ms idle iteration,
plus inbound/reply checks and five-second renewals. Measure this load and pool
contention before rollout; this implementation is not a capacity acceptance claim.
Any optimization must preserve explicit validation/expiry bounds.

Durable application writes, buffered-media persistence, background turn work and
Home Assistant command dispatch still need commit-boundary generation semantics.
A point check followed by an action cannot fence that action atomically. Shared
turn-state concurrency and durable state retention also remain work.
Managed pairing descriptors, physical co-presence, attestation invocation and
entitlements remain disabled/unimplemented at their existing boundaries.

Tests cover rejected admission, idle replacement on both channels, stale inbound
frames, channel-safe teardown, per-reply guards, independent stalled renewal,
normal cancellation, monotonic expiry and late-response rejection. A PostgreSQL
case exercises runtime handles on two independent pools and verifies stale
dispose cannot affect the replacement or the other channel. CI database tests,
not local skips, are the database acceptance evidence.
