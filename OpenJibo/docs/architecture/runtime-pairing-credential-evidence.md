# Runtime pairing credential and live-session evidence

This source slice prepares trusted inputs for the managed challenge-initiation
endpoint. It does not implement its runtime signer/client, select a physical
robot, establish co-presence, assign ownership, or enable pairing on serving
traffic. The managed endpoint remains a separate default-disabled profile.

## Credential evidence at verification and issuance

The existing 16-hex legacy credential fingerprint is telemetry/continuity
metadata. It must not become a managed device-registration fingerprint.
`AwsSigV4VerifiedProof` now privately retains a SHA-256 identity of length-prefixed
UTF-8 access-key ID and the secret revision that actually verified the signature.
Each UTF-8 component is prefixed by its four-byte big-endian byte length.
The binding is HMAC-SHA256(key, UTF8("openjibo-managed-pairing-credential-v1\0")
followed by that identity hash); base64url is unpadded.
Raw credential material and this intermediate hash have no public getters and
are not written into token metadata, responses or diagnostic strings. The
private proof can produce only a domain-separated HMAC binding with a dedicated
key. Changes to either credential component change the binding.

`OpenJibo:ManagedPairingCredentialEvidence:Enabled` defaults to false. Its
`HmacKey` must be canonical unpadded base64url for 32–64 random bytes, distinct
from replay, lease and other environment keys. Disabled, missing or malformed
configuration cannot produce evidence and has no fallback key. The raw option
is cleared after construction and is not registered for dependency injection.
No deployment or key installation accompanies this change.

When the option is ready, the ordinary CreateHubToken handler captures the
32-byte binding only after credential signature verification succeeds. It stores
`runtimePairingCredentialVersion=1` and canonical base64url
`runtimePairingCredentialBinding` with the durable token. Existing tokens are
not backfilled from today's account, a short fingerprint, wire device IDs or
CONTEXT metadata. Unverified compatibility tokens have no evidence. Existing
replay admission and disabled-profile token behavior remain unchanged.

Credential-only legacy signatures may create credential evidence. This does
not authenticate their payload or operation, make them replay-admitted, or
prove physical identity. The binding is constant for the same credential and
environment, regardless of unsigned body IDs, signed timestamp or request
signature. Shared account credentials therefore remain ambiguous between
physical robots and require an explicit trusted registration decision.

## Authoritative eligibility read

`ReadHubPairingCredentialEvidenceAsync` requires the exact issued bearer,
original Hub credential binding and dedicated evidence key. Unsupported store
implementations default to rejection. The in-memory store rejects case aliases;
the PostgreSQL store reads uncached durable token and current account state,
requires a live Hub token and matching original metadata, and recomputes the
HMAC using the current full credential. Missing, malformed, substituted or
removed evidence, revocation/expiry, account removal, credential changes or
an environment-key change all reject. The read cannot upgrade an old token
by deriving evidence from a newly rotated account credential.

This is a store primitive with forwarded cancellation. Its future caller must
bound the whole operation, retain the original evidence, and recheck it before
signing/dispatch and accepting a descriptor. It is not an atomic guard over an
external HTTP request or a later assignment transaction. Existing ordinary Hub
continuity retains its separate compatibility contract.

## Live-session binding

The active internal Listen lease handle can now derive a 32-byte session
binding after a fresh authoritative lease check. It hashes a versioned framed
domain, the opaque environment-scoped token digest, channel, private owner UUID
and retained generation. Renewing one handle preserves its identity; a new owner
or generation changes it. The method rejects proactive channels, cancellation,
disposal, expiry, provider failure and replacement, and does not reuse idle
validation. A hash alone is not evidence of liveness; the fresh check and the
trusted active handle supply that evidence. Check-then-action races still require
pre-dispatch/post-response checks and commit/receiver fencing where applicable.

The older completion signer's token-only session helper remains unchanged for
its existing contract. It cannot substitute for this active-generation binding.

## Next integration and acceptance

Next: connect these primitives to the active Listen connection and a separate
default-disabled initiation signer/HTTPS client. Authenticate a pinned managed
origin, prohibit redirects, bound request/response/time, validate exact descriptor
fields and request/session bindings, and treat uncertain responses as failures
without retry. No robot/browser-supplied binding or lease generation may enter
that trusted context. Then implement authenticated authorization status delivery,
usable human confirmation, current-generation completion and atomic assignment,
credential delivery and entitlement integration.

No managed credential registration is created here. The trusted registration
workflow must use this exact full binding, review shared-account ambiguity, and
coordinate credential/environment-key rotation. The candidate managed schema
and production privilege/release cut retain their separate activation gates.

Tests exercise actual SigV4 verification and token issuance, disabled/malformed
configuration, canonical metadata, secret/environment changes, compatibility
aliases, fresh Listen lease checks and failed/expired handles. PostgreSQL CI
supplies two independent facades and proves remote token/metadata changes and
same-ID secret rotation bypass positive local caches. Local database skips do
not count as database acceptance.
