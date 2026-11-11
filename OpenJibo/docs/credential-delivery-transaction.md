# Local credential handoff transaction

`scripts/bootstrap/credential-delivery-transaction.js` is a local POSIX file
transaction library with ES5 syntax for Node 6.9.2 and newer. It has no standalone
execution interface, network transport, service restart, mount, enrollment API or
implicit device path. Running the file directly refuses without opening credentials.
All qualification uses synthetic credentials and disposable directories.

An operator adapter still needs a separately reviewed device, recovery path,
physical association, ownership ceremony, stopped credential consumers, delivery
channel and runtime mapping. No such physical adapter is enabled by this library.

## Transaction contract

The caller supplies an explicit absolute directory containing `credentials.json`.
The directory must resolve without aliases, belong to the caller, and prohibit
group/other writes. The existing file must be an ordinary, single-link, caller-owned
0600 file with the directory's group for v1. The explicit stock v2 profile below
accepts only root:root 0666 originals and installs root:root 0600 replacements.
The journal is created 0700. Windows mutation
refuses; Windows tests do not count as POSIX acceptance. The operator must already
own and exclusively control the directory and its ancestors for the entire call.
These checks are not protection against another process with the same OS authority.

The exact plan has `schema=openjibo.credential-delivery.v1`, a 32-character lowercase
hex `operationId`, and four lowercase SHA-256 values: `beforeSha256`, `afterSha256`,
`mappingEvidenceSha256`, `recoveryEvidenceSha256`. The last two bind reviewed
references to the transaction; supplied hashes authenticate neither evidence nor
ownership, recovery readiness, subscription state or a physical device. Keep plans,
status and original credential backups in operator-controlled storage.

`create(directory)` returns four methods:

1. `prepare(plan, replacementBuffer)` validates bounded flat UTF-8 credentials,
   refuses duplicate/escaped duplicate fields and unsupported shapes, and retains
   exact original/replacement bytes and the plan before touching the live file.
   At least the access ID or secret must change. The region must remain identical;
   endpoint conversion and credential handoff are separate operations.
2. `activate(plan)` requires intact journal bytes and an exact current original
   hash, writes/fsyncs an exclusive temporary file, rechecks the target, renames it,
   fsyncs the containing directories and verifies the installed bytes. An exact
   installed repeat reports the observed state without rewriting the file.
3. `inspect(plan)` reports `staged`, `installed`, `restored` or `diverged` and
   whether a writer lock was observed. It never exports credentials or hashes.
   An incomplete/corrupt journal refuses instead of reporting a successful stage.
4. `restore(plan)` accepts only the exact installed or original bytes. It restores
   original bytes through the same replacement mechanism and records terminal
   restoration. Restored operations cannot be reactivated; use a new reviewed plan.

Journal/backup writes are exclusive, bounded, 0600 and fsynced. A same-filesystem journal
holds both complete versions before the target rename. Status and errors contain
no credential values, input hashes or paths. The statuses always leave
`ownershipVerified`, `recoveryVerified`, `robotContactPerformed` and
`serviceAccessGranted` false. An installed file is not an acknowledged delivery,
proof that a service loaded it, or permission to use paid features.

## Interrupted and ambiguous operations

A process-scoped operation creates `.credential-delivery-lock` exclusively. Normal
returns/exceptions remove only that lock; process death leaves it for review.
No call steals a stale lock or deletes a journal, partial file or previous backup.
A complete matching pre-rename temporary file can be resumed after operator review;
an incomplete file refuses and is retained. Unexpected current bytes are never
overwritten. Do not treat an exception as evidence that replacement did not happen:
inspect independently before deciding what to do next.

Before an operator clears a stale lock, stop the known writer and credential
consumers, establish that no other writer remains, retain the journal and compare
the current file through an authorized read. Clearing a lock is deliberately absent
from the API. The caller must keep a recovery route that works with both versions
denied, and must reconcile runtime mapping separately. An exact byte rollback of a
local file must not automatically restore revoked cloud authority or clear a hold.

## Explicit stock permission profile

`schema=openjibo.credential-delivery.v2` adds exactly
`filePermissionProfile=stock-root-0666-to-0600` to the v1 plan. It is an opt-in
root:root profile: the caller UID and directory UID/GID must all be zero, and
the original must be a regular single-link root:root file with exactly 0666 mode.
The observed stock parent mode 0755 is supported; directory UID/GID, inode, device and mode are rechecked throughout the operation. Other file modes, special bits, arbitrary owner IDs and implicit upgrades from v1
are refused. V1 still accepts only 0600; it never silently selects stock behavior.

Preparation preserves the original bytes and permissions. Private backups and
the plan stay 0600 under the 0700 journal. Activation atomically installs the
new credential as 0600. Restoration creates a private temporary copy, sets its
exact original 0666 mode while it is still inside the protected journal, fsyncs
it, then renames it into place. No chmod is applied to the live inode. Abort and
rollback preserve exact original bytes, UID, GID and mode; a terminal restoration
cannot be reactivated. Unexpected permission/owner/content changes refuse.

Lstat, opened-fd metadata and post-read metadata must agree. Inode, device, size,
UID/GID, complete mode, link count, mtime and ctime are compared; replacement
rechecks the current file identity immediately before rename. Preparation also
rechecks after durable backup. These checks require independently quiesced writers
and exclusive operator control. They are not an atomic compare-and-swap defense
against hostile concurrent writers, including an already-open writable descriptor
to a legacy 0666 file. Do not infer quiescence or physical/customer ownership from
root UID, a file hash or the supplied evidence references.

Qualification requires `sudo -n NODE scripts/bootstrap/test-stock-credential-delivery.cjs`
on disposable Linux files as well as a non-root refusal run. Eighteen groups and
88 before/after mutation-fault cases include ownership/mode drift, inode replacement,
private-backup protections and process death after the restoration rename. The v1
suite remains mandatory. The frozen `credential-delivery-transaction-v1.js` retains
the original `9e60fa057a4ee05e58f16a53f76c09e623a339189ca0889daa5a358e1fffb239`
bytes for previously pinned synthetic rehearsals; it is not the stock-profile
implementation. The default library still has no standalone device writer.

File/directory fsync errors refuse; no weaker durability mode is selected. POSIX
filesystem behavior and storage hardware still govern power-loss durability.
The tests inject errors and kill a process after rename; they do not emulate board
power loss, a full-image restore, filesystem corruption or physical recovery.

## Acceptance

Run `node scripts/bootstrap/test-credential-delivery-transaction.cjs`. Linux CI
requires both Node 6.9.2 and Node 24 runs and retains the synthetic result plus exact
reviewed source. Coverage includes exact rollback, terminal abort, unknown edits,
context substitution, corrupt backup/manifest, unsafe files/roots, short/failed
writes, stale-lock refusal, real child-process death after rename, and injected
failures before/after every mutating filesystem call in the successful lifecycle.
No physical inventory, customer data, provider credentials or vendor image is used.
