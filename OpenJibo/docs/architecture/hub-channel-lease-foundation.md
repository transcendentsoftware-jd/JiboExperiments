# Hub channel lease foundation

The durable lease primitive now has a default-disabled
[coordinator transport lifecycle](hub-channel-lease-lifecycle.md). This page
describes the storage contract and integration limits; the lifecycle page records
the caller, configuration, conservative deadlines and remaining activation gates.

## Storage contract

`IHubChannelLeaseStore` and migration `018_hub_channel_leases.state.sql` provide
one durable slot per opaque token digest and channel: `Listen=1`, `Proactive=2`.
Stock clients can use both channels with one Hub token. Replacing a listen slot
does not replace the proactive slot. A token digest must be an environment-scoped,
domain-separated HMAC of the exact bearer, with the same stable key on every
participating replica. The caller must authenticate the token before acquisition.
The adapter accepts only a 32-byte digest; it does not authenticate its provenance
or derive one from a bearer. The opt-in coordinator policy performs that derivation;
no production key or enabled live profile is installed by source implementation.

The single `public.ManageHubChannelLease` function exposes four operations through
a dedicated function-only connection:

| Operation | Result |
| --- | --- |
| Acquire | Replace that slot, increment its generation and set a fresh 30-second expiry |
| Renew | Extend expiry only for the exact owner/generation while still unexpired |
| Check | Return the exact live owner/generation or an empty result |
| Release | Expire the exact live owner/generation, retaining its generation tombstone |

The connection owner is a fresh, nonempty UUID for each connection attempt. The
database serializes all operations for a slot with a transaction advisory lock,
then reads server time. A renewal that expires while queued cannot resurrect the
lease. Owner and generation must both match; an old socket cannot renew or release
a successor, even if its owner UUID is accidentally reused. Generation overflow
fails the transaction rather than wrapping or replacing ownership.

Tombstones are intentionally retained after release and expiry. Deleting rows,
restoring an older database or changing the digest key can reset/split the
generation namespace. Do not add ordinary expiry cleanup to this table. A future
retention/key-rotation plan must prove no old handle or protected operation can
still use the retired namespace. Storage grows with distinct token/channel slots;
capacity and safe retirement remain deployment work.

The table stores only the opaque digest, channel, generation, random owner and
expiry. It contains no raw bearer, credential, account, robot or ownership claim.
The migration revokes PUBLIC table/function access and creates no roles or grants.
Deployment must review a restricted function owner, dedicated EXECUTE-only login
and inability to alter/delete the underlying rows or assume the owner role.
The adapter uses a separate two-connection pool, two-second connection/command
timeouts and certificate plus hostname verification outside loopback. It does
not fall back to cloud-state, replay observer or administrator credentials.

Calls forward cancellation and perform no retry. An exception or lost response
may follow a committed acquisition/renewal/release. Callers must treat that as
uncertain and stop protected work. Acquisition is deliberately not idempotent:
repeating it increments the generation again. A successful check is only a
point-in-time observation; it cannot fence a later side effect on its own.

## Coordinator integration requirements

The opt-in lifecycle implements bounded acquisition, monitoring, transport checks
and channel-local teardown. Complete activation still requires the following
end-to-end contract, especially generation predicates at durable commit boundaries:

1. Revalidate the durable Hub-token binding, derive the opaque token digest and
   acquire the correct channel slot before accepting protected work. Reject
   unavailable or uncertain acquisition without entering compatibility mode.
2. Keep a connection-local owner/generation handle. Renew independently of a
   blocked receive or reply operation, with a local conservative expiry deadline;
   abort/cancel on replacement, expiration, timeout or provider failure. A cached
   lease must never remain usable beyond its deadline during database failure.
3. Check generation at inbound, idle/watchdog, progress and final reply boundaries.
   Treat these as bounded transport checks, not atomic fencing of side effects.
   Durable writes and external actions need generation predicates/transactional
   integration at their own commit boundary; a check followed by work has a race.
4. Make session metadata, buffered-audio cleanup and session closure respect
   channel plus generation. The current process-local active connection ID is
   not a cross-replica fence, and the two channels may share session state.
5. Release only the exact handle during teardown and bound the release wait.
   Failed or stale teardown must never erase successor state. Account/token
   revocation remains separately enforced by the credential continuity monitor.

Activation still requires rollout policy, all-replica namespace coordination,
load/failure measurements, staging replacement/reconnect tests and stock-device
acceptance. Tokenless isolated mode, unbound compatibility tokens, API sockets and
Home Assistant have explicit compatibility boundaries in the opt-in lifecycle.
No live deployment/configuration change, database grant, managed pairing,
attestation invocation, co-presence or physical ownership accompanies this source.

## Validation

PostgreSQL tests use independent pools and 24 concurrent acquisitions, verifying
unique increasing generations and exactly one current owner. They cover channel
coexistence, stale-owner renewal/release, owner reuse, expiry, expiry during a lock
wait, malformed calls, overflow rollback, denied execution before an explicit
grant and function-only privilege isolation with a shadowing temporary table.
Adapter tests reject invalid inputs and insecure
remote connections before opening a database, and honor pre-cancellation.
Database tests run in the nonparallel shared-schema collection; local skips are
not database acceptance. CI supplies disposable PostgreSQL.
