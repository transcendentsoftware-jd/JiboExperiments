# Optional runtime access authorization

The runtime can delegate robot service access to an independently trusted HTTPS
authorization service. `OpenJibo:RuntimeAccess:Enabled` defaults to `false`.
Self-hosted operation does not require a subscription or this service. Merging
this implementation does not install keys, enroll hardware or enable traffic.

## Configuration and identity

The independently configured profile contains `ManagedOrigin`, `Issuer`,
`Audience`, `KeyId`, `PrivateKeyPem`, `LifetimeSeconds` (1–10), and an `Identities`
dictionary. The origin must be HTTPS with no path, query, credentials or fragment.
The signing identity must be a private P-256 key. No pairing, replay, channel or
session signing key is substituted for missing configuration.

Each identity entry is keyed by the full, canonical 32-byte registered credential
binding in base64url form. Its `RobotId` and canonical runtime `DeviceId` are
operator-verified configuration, never robot-supplied request fields. The service
must independently derive the same identifiers from its credential registry.
Neither a short credential fingerprint nor a body/header robot ID can authorize
access. Absent, inconsistent or stale mappings deny access.

The existing `OpenJibo:ManagedPairingCredentialEvidence` dedicated HMAC key is
needed to derive and revalidate the full credential binding. Hub Listen and
Proactive also require the separate authoritative channel-lease profile.
Notification tokens issued under this profile retain verified credential evidence;
older tokens without it must be replaced. The persistent reader checks the current
token/account state directly, across replicas, including secret rotation.

## Request and response contract

`POST /v1/runtime/access-check` receives only `{ "assertion": "…" }`.
ES256 headers use `typ=openjibo-runtime-access+jwt`; payload purpose is
`openjibo-managed-runtime-access`. Exactly ten payload fields are accepted:
`version`, `purpose`, `iss`, `aud`, `credential_binding`, `session_binding`,
`operation`, `iat`, `exp`, `jti`. Version is 1; bindings and nonce are canonical
32-byte base64url. Operations are `hub-token`, `hub-listen`, `hub-proactive`,
`robot-socket`, and `protocol-http`. Each query uses a fresh random nonce.

The service authenticates its caller, consumes replay evidence, and derives
current authority itself. A successful 200 JSON response has exactly `robotId`,
`deviceId`, `credentialBinding`, `sessionBinding`, `requestDigest`, `operation`,
and `expiresAt`. `requestDigest` is base64url SHA-256 of that request's JTI.
Both identity fields must match configured identity; all correspondence fields
must match the fresh request. Expiry must be UTC, future and at most five seconds
away. Responses require `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
Other responses cannot grant access.

The client bounds headers and body to two seconds and 4 KiB, disallows redirects,
cookies, decompression and custom certificate trust, and never retries a query.
A timed-out query may have committed replay evidence; no response is reconstructed.

## Enforcement and reconnect

Protocol HTTP checks precede identity observations and dispatch side effects.
Signed request evidence or a current credential-bound bearer must resolve to the
configured device. Conflicting presented identities are rejected. The authorized
envelope's device identity comes from configuration, including when an untrusted
request omitted its device field. This access gate supplements existing protocol
authentication; it does not make unsigned request bodies operation-authenticated.

Each accepted robot socket gets a new connection-local random binding and fresh
decision. Grants are never shared between connections, tokens or channels. The
original credential and channel authority are checked before and after refresh;
late responses cannot revive stale authority. Grant time is bounded by both UTC
and a monotonic clock, conservatively capped at five seconds from request start.

Every inbound work boundary and every send, including notification-registry
pushes, uses the connection guard. Short positive grants avoid one authorization
request per audio frame. A watchdog refreshes expiry on idle connections; failed
refresh cancels connection work and aborts the socket. No new work or send starts
on an expired grant while refresh is pending. Already-started external work cannot
be rolled back by this transport policy. Reconnect always obtains a new decision,
even if an old connection recently had access.

## Deployment boundary

An installation can start in the separate [closed maintenance profile](runtime-maintenance.md)
without initializing runtime data/provider services. Liveness in that profile
does not establish access readiness. Opening normal service remains an explicit
configuration and restart operation.

The coordinator acceptance suite covers idle suspension, authorization outage,
unready authority and fresh reconnect independently for Listen, Proactive and
notification sockets. These are owned-fixture software checks, not hardware
certification. Use the [read-only runtime intake](robot-runtime-intake.md) before
planning a device-specific configuration change.

This profile serves already enrolled, credential-mapped robots. It provides no
unpaid enrollment or recovery exemption. Those journeys need their own separately
authenticated routes. The legacy Home Assistant proxy WebSocket uses another
authentication model and is rejected on a listener with this profile enabled;
its compatible authenticated integration remains a release gate. Tokenless LAN
Hub compatibility is not an access-granted path under this profile.

Do not enable the profile on an existing robot until the selected provider,
credential mapping, HTTP operations, both Hub channels, notification pushes,
suspension, ownership transfer and reconnect have passed the intended hardware
cohort. A health response alone does not verify authorization readiness.
