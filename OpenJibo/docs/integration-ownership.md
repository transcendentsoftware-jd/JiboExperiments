# Private integrations when a robot changes accounts

When a robot is paired to a different portal account, its new owner sets up
calendar feeds and Home Assistant again. The new portal session cannot read the
previous owner's feed URLs, Home Assistant command secret or link. Pairing again
to the same account preserves settings, ownership generation and existing robot
sessions. Returning to an earlier account does not restore its old integrations
or old sessions; configure fresh connections.

The first account claim of a previously unclaimed robot adopts that robot's
existing integration namespace. It still invalidates its earlier code-only
portal sessions. This differs from a transfer between two existing accounts.

## Stored records and connections

New integration records carry the robot's ownership generation. Older records
without this field remain bound to the original legacy generation, which is
frozen when ownership changes. Reads do not rewrite encrypted files. Inactive
records remain encrypted for backup/recovery and are filtered from runtime and
portal reads; this feature is not secure erasure. It does not delete shared
household/loop data or reset the robot itself.

Portal reads and new private writes carry the signed session's generation.
An old request cannot select or overwrite the new generation's integration
record. Calendar fetch completions target their original feed ID, so a delayed
old fetch cannot change a replacement feed's status. This does not cancel an
external fetch or a command that was already sent before transfer.

Home Assistant must retain its opaque link ID to resume a pairing. An instance
name alone now receives a fresh verification code and never a saved command
secret. A new pairing issues a new link and secret. Transfer revokes inactive
connections on the pairing API instance. Other instances recheck durable
ownership before treating a connection as active, dispatching commands or
accepting results. Revoked sockets are aborted when checked. This is not an
immediate cross-process socket-close broadcast. Results are bound to their
original socket, and an old socket's cleanup cannot remove its replacement.
The encrypted integration file still requires its established single-writer
storage discipline; PostgreSQL ownership checks do not make file snapshots a
distributed integration database.

## Upgrade and recovery

The implementation uses version 3 portal sessions, integration snapshot schema 3
and PostgreSQL migration 019. Sign in again once after upgrading, even when the
same private signing key is retained. Schema 1/2 integration files are read
without rewriting their bytes; the next explicit mutation saves schema 3.
Malformed schema 2/3 files and wrong encryption keys still fail closed and
preserve the existing file. Older runtimes that support only schema 2 refuse a
schema 3 file rather than silently ignoring ownership metadata.

Stop all API writers before upgrade or recovery. Keep the original encryption
keys, signing key, database and integration file together in the full backup.
For rollback, restore the complete pre-upgrade state with the matching old
runtime; do not run old and new ownership logic against shared live state.
Follow the [recovery guide](self-hosted-recovery.md), including its corrected
legacy path `/app/api/App_Data/user-integrations.json` for standard images.

These source changes do not alter already published image digests. In
particular, `runtime-preview-41757874916` predates this integration-transfer
policy. Native acceptance of that preview does not certify these new changes.
