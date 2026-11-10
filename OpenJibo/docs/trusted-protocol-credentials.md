# Trusted protocol credentials

The optional trusted credential registry lets one runtime verify several
independently provisioned legacy SigV4 credentials. It supplements the
[runtime access policy](runtime-access-authorization.md). It does not enroll a
robot, establish physical possession, assign ownership or grant service access.

## Configuration and admission

`OpenJibo:Security:TrustedProtocolCredentials` contains `Enabled` (default
`false`) and a `Credentials` list. Every entry has `AccessKeyId` and
`SecretAccessKey`. Supply these through the operator's protected configuration
mechanism. Do not commit real credentials, put them in instruction pages, or
derive this list from observed traffic, request bodies or inventory fingerprints.

With the registry disabled, the existing shared-account/self-hosted verifier
behavior is preserved. With it enabled, only complete, case-sensitive access IDs
in the registry can select a secret. There is no shared-account or extra test
credential fallback. Unknown IDs, invalid signatures and unbound old tokens are
denied. An invalid inventory denies all entries, including otherwise valid ones;
normal host registration refuses it with a sanitized error.

The inventory permits 1–256 entries, unique IDs and distinct secrets. IDs are
1–128 visible ASCII characters excluding `/` and `,`. Secrets are 16–256 visible
ASCII characters. Whitespace/control characters and missing values are refused;
values are not silently trimmed. A valid dedicated
`OpenJibo:ManagedPairingCredentialEvidence` key is also required. Options are not
registered as injectable raw-secret objects, and diagnostic string formatting of
credential objects is redacted. This does not make arbitrary serialization of a
secret object safe.

## Tokens and current authority

Successful verification produces existing environment-keyed, full-credential
evidence, including the secret revision. The registry carries no device ID.
The configured access service independently maps that opaque evidence to a
device and makes current authorization decisions. A short inventory fingerprint
cannot choose a secret or establish authority.

Both Hub and notification tokens retain their original credential binding and
full evidence. Fresh token reads, pairing evidence reads and channel lease checks
validate that evidence against the current registry. File-backed and PostgreSQL
stores refuse removed credentials, changed secrets, inconsistent fingerprints,
missing evidence and old unbound tokens. PostgreSQL reads the durable row rather
than trusting a cached positive session. Managed HTTP uses the same configured
verifier before identity observations or provider work.

Existing stock signatures may authenticate only credential possession. Enabling
this registry does not make an unsigned body operation-authenticated. Keep the
separate identity, ownership, replay, entitlement and channel controls enabled
where required by the selected deployment profile.

## Removal, rotation and reconnect

The registry is an immutable startup snapshot. Changing a configuration file or
environment value does not change a running process's authority. Use the
[closed maintenance profile](runtime-maintenance.md) and the existing controlled
deployment procedure to retire every old replica before reopening with a new
inventory. A surviving old replica still has its original snapshot; this feature
does not claim immediate fleet-wide revocation or a durable revocation ledger.

To remove a credential, omit it from the replacement inventory. To rotate it,
replace its secret or provision a new access ID and secret. Do not disable the
registry flag as a removal procedure: that selects the legacy verifier after
restart. An enabled empty inventory refuses normal startup; keep the host closed
when removing the final credential.

The new instance rejects prior bearer tokens from that credential. Unchanged
credentials keep working. A rotated credential needs a fresh signature and a
fresh issued token. Rotation also changes its opaque evidence, so any provider
mapping needs independent review and update; authority is not inherited merely
because a replacement uses the same access ID. Do not restore an old secret as a
substitute for a fresh rotation or bypass another service's hold/review policy.

## Acceptance boundary

Synthetic tests cover full-ID selection, rejected shared-account/extra fallback,
all-or-nothing validation, redaction, startup refusal, immutable snapshots,
durable removal/rotation, two independent HTTP identities and reconnect on Listen,
Proactive and notification sockets. Owned PostgreSQL acceptance exercises fresh
facades over retained tokens after removal, secret rotation and ID rotation.
Mandatory CI evidence refuses skipped registry/database acceptance cases.

These are software fixtures, not proof of a physical robot's credential identity,
delivery or recovery. No real credential is supplied by this change. Hardware
intake, safe per-unit credential delivery, supported firmware, ownership proof
and physical recovery still require separate acceptance before enabling it on a
robot or publishing a managed service.
