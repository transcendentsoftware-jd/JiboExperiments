# Strict Hub-token replay admission

This opt-in strict issuance profile is disabled by default. It does not enable
managed pairing or replace the best-effort replay observer. Captured stock
requests with an unbound body or unsigned target are not eligible. Do not enable
the profile on stock traffic without a reviewed compatibility/deployment decision.

## Request boundary

`Account.CreateHubToken` now has an async dispatch path that awaits admission
before token issuance. The synchronous handler entry point uses the same gate.
With the setting omitted/false, compatibility behavior is unchanged. When
enabled, the existing finite CreateHubToken policy must authenticate payload,
target, scope, host and credential. Missing/weak/stale proof or replay gets the
same generic 401. Invalid configuration, unavailable storage or uncertain
consumption gets a generic 503. Neither issues a token. Other account and
notification operations are unchanged.

Immutable `AwsSigV4VerifiedProof` records the authenticated operation and signature
time. Altering the public observation record cannot upgrade credential-only
proof, change its time or retarget notification proof into Hub admission. Only
environment-keyed HMAC digests cross the storage boundary; raw access keys,
secrets and signatures do not.

Admission has a two-second bounded async wait, forwards request cancellation
and never retries automatically. A lost response may already have committed;
the request is unavailable, not successful issuance. The client must sign a
fresh request. Consumption and token persistence are separate transactions:
a process/issuance failure after consumption does not undo that reservation or
return a cached token.

## Atomic storage and rotation

Migration `017_hub_token_replay_admission.state.sql` adds separate
`public.HubTokenReplayAdmissions` and `public.ConsumeHubTokenReplay` objects.
It leaves the observation table, publisher, classifications and roles unchanged.

The function consumes one current digest or a current/previous pair atomically.
It takes a total order over actual advisory-lock IDs, checks every slot, then
reserves all slots or none. Any live overlapping slot rejects the whole batch.
Key versions are metadata, not part of the digest primary key; changing a
version cannot reuse a consumed digest.

The database checks signature time after lock acquisition. Both application and
database enforce the five-minute skew policy; expiry is signature time plus
five minutes. Proof that expires while queued is rejected. Opportunistic
cleanup removes at most 128 expired rows using `SKIP LOCKED`.

Rotation requires overlapping material on live replicas: deploy B+A while old
replicas use A, drain old-only replicas, then retain overlap through the full
acceptance horizon. Signatures may be five minutes in the future, so the worst
horizon is ten minutes after the last old-only consumption, plus in-flight and
clock margins. Disjoint key sets, premature retirement and changed material
under a reused version are not solved by this function. Distribution, stable
version/material bindings and retirement need deployment review and tests.

## Configuration and privileges

Section: `OpenJibo:Security:HubTokenReplayAdmission`.

| Field | Contract |
| --- | --- |
| `Enabled` | Default false; true selects strict issuance |
| `ConnectionString` | Explicit dedicated function-only identity; no fallback to cloud-state or observer credentials |
| `HmacKey`, `KeyVersion` | Environment-specific material of at least 32 bytes, base64url encoded, and a positive version |
| `PreviousHmacKey`, `PreviousKeyVersion` | Optional complete pair with different material and version |

Enabled but incomplete/invalid bound configuration resolves to unavailable,
never disabled compatibility. Normal configuration-binding errors may prevent
startup. The dedicated pool has at most two connections and bounded connection/
command timeouts; non-loopback connections require TLS.

The function is `SECURITY DEFINER`, has a restricted search path and qualifies
its objects. Migration grants no table/function access to PUBLIC and creates
no login, role or API-host provisioning. Before enablement, review ownership,
transfer the function to a restricted non-login owner as appropriate, and
provision/test a dedicated EXECUTE-only identity. It must not read/delete the
table, create public objects or assume its owner role. Do not use administrator
credentials to make this profile pass. Rollback must not delete replay evidence.

The existing host/target allowlist remains fixed. A custom provider or staging
hostname outside it needs separate policy review; this setting does not make
HTTPS or a header value into provider admission or physical ownership.

## Validation and remaining work

Tests exercise real signing/verifying, concurrent duplicate issuance, both
handler entry points, forged record flags, wrong operation/time, storage loss/
timeout, cancellation, invalid keys and default compatibility. PostgreSQL tests
use separate pools for concurrency and overlapping reversed batches, ensure
rejected batches do not reserve new slots, test clock/lock-wait rejection and
malformed input, and exercise a function-only role with a shadowing temp table.
CI supplies isolated PostgreSQL; skipped local database tests are not acceptance.

Implementation performs no deployment, signing-key installation, role grant or
stock-robot test. Next is durable live-session fencing and co-presence. Fencing
must distinguish listen and proactive channels: stock clients can open both
with one Hub token. A single global per-token lease would break that flow.
The [live continuity monitor](hub-token-live-continuity.md) still validates
credentials only. Neither component assigns ownership or calls the disabled
managed attestation issuer.
