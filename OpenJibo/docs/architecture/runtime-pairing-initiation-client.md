# Runtime pairing initiation client and connection context

The neutral runtime now has a separate initiation signer and bounded HTTPS client,
exposed through a server-created context on eligible Listen connections. It is
**default-disabled**. No voice phrase, browser route, automatic enrollment,
co-presence flow, completion or assignment is enabled by this source change.

## Trust and configuration

`OpenJibo:ManagedPairingInitiation` has `Enabled` (false by default), `ManagedOrigin`,
`Issuer`, `Audience`, `KeyId`, `PrivateKeyPem` and `LifetimeSeconds` (30 by default,
1–60 allowed). Origin must be an absolute HTTPS origin with no credentials,
path other than `/`, query, fragment, backslash or surrounding/control whitespace.
Requests always use `/v1/runtime/pairing-challenges`. Normal platform TLS chain,
hostname and revocation checks apply; there is no custom trust bypass. The handler
prohibits redirects, cookies and default credentials, and has no retry/logging
handlers. The signer imports a P-256 private key; the raw option is cleared after
construction and is not registered in DI. Initiation does not fall back to the
existing completion-signing configuration.

An eligible context additionally requires the dedicated full
[credential evidence profile](runtime-pairing-credential-evidence.md), an active
Listen [channel lease](hub-channel-lease-lifecycle.md), an exact issued Hub bearer,
and original credential/evidence metadata. Disabled, misconfigured, tokenless,
old/unbound, proactive or unleased connections have no pairing context. Their
ordinary compatibility behavior is unchanged. Enabling only the HTTP client
does not supply the other prerequisites or authorize enrollment.

The coordinator binds `IRuntimePairingSessionAccessor.Current` in the connection's
async execution context. Trusted server workflows can call its `BeginAsync`;
there is no caller-supplied robot ID, credential identity, owner or generation.
Concurrent connections and nested scopes are isolated. Connection close cancels
the retained session capability and clears mutable ambient holders inherited by
background work. A context is not a human-intent detector or proof of co-presence.

## One guarded attempt

A connection context permits one attempt. Concurrent or subsequent calls are
rejected locally; it never retries a lost, rejected, limited or uncertain attempt.
This conservative limit is not a finished customer recovery UX. Reconnecting
must not be treated as proof that an earlier challenge was rolled back or that
retry is safe. Pending-pairing expiry/status/recovery and deliberate later attempts
remain part of the future ceremony design.

Before dispatch, the context re-reads the original token/account credential
state using the dedicated evidence key and performs a fresh check through the
original Listen lease handle. The signer emits the exact nine-claim managed
initiation contract: version/purpose, issuer/audience, full credential binding,
current owner/generation session binding, integer issue/expiry seconds, and a
fresh random 32-byte JTI. It uses ES256 with a P1363 signature and canonical
unpadded base64url. Its JWT type and purpose differ from completion assertions.
No raw legacy secret goes to the managed service.

The HTTP exchange has a two-second total async bound, including response bodies;
the connection operation has a six-second overall budget. Authority reads have
a two-second bound and forward linked caller/connection cancellation. A resolver
that finishes after its deadline cannot start a late request. The source does not
claim hard realtime scheduling guarantees or atomic fencing of external effects.

## Strict response and failure contract

The client reads at most 4,096 response bytes, including unknown-length bodies,
and refuses content encodings. A successful 201 must be JSON, no-store and
no-referrer, with exactly these eight fields and no duplicates:

| Field | Validation |
| --- | --- |
| `pairingId`, `deviceKeyId` | Nonempty canonical GUIDs |
| `credentialVersion` | Positive integer |
| `pairingHandle`, `robotChallenge` | Distinct 32-byte lowercase hex secrets |
| `sessionBinding` | Exact binding of this signed request |
| `requestDigest` | Exact base64url SHA-256 of this request's JTI |
| `expiresAt` | Unexpired UTC instant, no more than five minutes plus 30 seconds of clock skew ahead |

After a created response, the context re-reads original credential state and
freshly validates the same lease binding again, checks expiry and cancellation,
and only then returns the descriptor. Replacement, revocation, secret rotation,
expiry or provider failure discards it. The descriptor is not stored in ordinary
session metadata, logs or telemetry. Diagnostic strings redact assertions,
handles, challenges and evidence.

Empty 401 means rejected. Empty 429 with an integral Retry-After of 1–600 seconds
means rate limited. Redirects, other statuses (including 503), malformed or
substituted responses, transport failure and timeout are uncertain; no descriptor
is released. Pre-dispatch authority failure is unavailable; loss of authority
after dispatch is uncertain. Caller/connection cancellation propagates cancellation.
A committed challenge can exist even when no descriptor is returned, including
when a connection closes during the request. No automatic cleanup, rollback or
ownership claim follows from any of these outcomes.

Fresh checks still have a check-then-action race. The future workflow must preserve
current-generation checks before robot presentation and completion, plus the
managed transaction/receiver semantics for assignment and credential delivery.
The existing token-only completion helper is not an active-generation substitute.

## Acceptance and next product work

Tests cover actual signature/claim construction, separate purposes, malformed
configuration/keys/origins, exact response/request joins, unknown-length bodies,
redirect refusal, no retry, bounded waits, late resolver/client responses,
credential/lease loss, concurrent calls and real coordinator scope lifetime.
Mock-handler tests are not live TLS, provider or physical-device acceptance.

Next: trusted credential registration (including shared legacy-account ambiguity),
a usable deliberate robot/browser initiation and confirmation workflow,
authenticated authorization-status delivery, current-generation completion using
the existing signer, atomic assignment/credential delivery/entitlements, and
staged provider/device/reconnect/recovery evidence. The managed schema candidate,
role/key installation and production release cut retain their separate reviews.
The self-hosted runtime remains usable with this profile disabled. Conversion
adapters, OS/CPU acceptance and linked setup/update/recovery/support documentation
remain parallel platform delivery tracks.
