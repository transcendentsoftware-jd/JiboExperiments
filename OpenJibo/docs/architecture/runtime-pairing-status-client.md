# Connection-bound pairing status reads

The runtime can explicitly read the status of a pairing created by its current
Listen connection. This is independently opt-in: set
`OpenJibo:ManagedPairingInitiation:StatusReadsEnabled` only after reviewing the
managed status profile, candidate schema and privileges. It defaults to false.
Initiation readiness, full credential evidence and the Listen lease prerequisites
still apply. There is no background polling or automatic enrollment trigger.

The status client deliberately uses the initiation client's configured HTTPS
origin and runtime signing identity, with a distinct status JWT type/purpose.
The managed service must separately enable `RuntimePairingStatus` and configure
its issuer/audience/public keys; initiation trust does not enable status trust.
The status issuer must match the original initiation receipt's issuer.

## Retained binding and fresh authority

After initiation succeeds and its post-response authority checks pass, the
connection context retains only pairing/key/version/session/expiry and the
SHA-256 challenge hash for status. It does not retain the raw challenge or handle
in that binding or write the binding into ordinary session metadata.
`IRuntimePairingSession.ReadStatusAsync` takes no caller-supplied descriptor or
identity. Before successful initiation, after expiry, or without status opt-in,
it cannot issue a status request.

Each explicit call rechecks the original full credential and freshly validates
the same Listen lease generation before dispatch. A new eleven-claim ES256 proof
binds the original pairing, full credential, session and challenge hash to a fresh
random JTI. The request is POST JSON with only `assertion` to
`/v1/runtime/pairing-status`. No proof or identity appears in the URL.

The existing HTTPS transport policy remains: normal platform TLS validation,
no redirects, cookies, default credentials, retry handlers or content encodings.
The HTTP exchange has a two-second bound and a 4,096-byte response bound,
including unknown-length bodies. A successful 200 must be JSON, no-store and
no-referrer and have exactly seven fields: `pairingId`, `deviceKeyId`,
`credentialVersion`, `sessionBinding`, `requestDigest`, `status`, `expiresAt`.
The original pairing/key/version/session/expiry must match exactly, the query
digest must match this request's JTI, and phase must be pending or authorized.
Explicit UTC expiry is required; no customer/household or challenge data is read.

After a successful response, the context again validates credential and lease
authority, expiry and cancellation before returning the snapshot. Lost authority,
foreign/substituted state, timeout or malformed data releases no snapshot.
Connection close cancels retained capabilities and revokes inherited contexts.
Fresh checks remain check-then-action bounds, not atomic external permission.

## Poll budget and meaning

There is at most one logical read in flight per connection and a five-second
minimum interval. A valid server Retry-After of 1–60 seconds applies from response
arrival, with at least the same five-second local floor. The backend admits at
most twelve accepted reads per pairing per sixty seconds. Each deliberate later
read uses a fresh nonce; there is no automatic retry loop, including after a
timeout whose nonce transaction may have committed.

The whole guarded operation has a six-second budget, with two-second authority
waits. Late provider results cannot release a snapshot or start a late request
after the operation's cancellation. Await bounds do not claim a hard realtime
or provider-force-termination guarantee.

Authorized means browser consent at the backend snapshot, not physical
co-presence, current completion permission, ownership or entitlement. A later
completion/assignment must perform its own fresh authority and transaction checks.
The existing one-initiation-attempt connection limit remains; reconnect does not
recover a lost descriptor or prove that an earlier pending challenge rolled back.

## Remaining delivery work

Trusted registration/shared-account decisions, deliberate robot/browser intent,
usable physical confirmation, current-generation completion, atomic assignment/
credential delivery/entitlements and staged provider/device/reconnect/recovery
acceptance remain. The real voice/background path and connection lifetime through
OIDC must be proven rather than weakening generation checks after reconnect.
This source change installs no key, role, migration or robot configuration and
enables no serving profile. Self-hosting remains available with the profile off.
