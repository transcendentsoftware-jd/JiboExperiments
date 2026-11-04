# Portal sessions and ownership transfers

The portal distinguishes account, selected-robot and administrator sessions.
An account login can list its paired robots, pair/select one and rename an owned
robot. Select a robot before opening its dashboard, calendars, loop members or
integrations. A device name does not grant administrator privileges.

Selected-robot and spoken-code sessions carry a signed ownership binding. Each
portal robot request checks the current durable user/device link. After a
committed first account claim or transfer, a proof bound to the previous link
receives HTTP 401. The new owner can pair/select normally. Account login itself
continues to work; the old account's inventory no longer includes that robot.
This check also applies on another application instance using PostgreSQL.

These checks govern portal HTTP requests. They do not retroactively cancel an
already-running request or certify protocol/socket authorization, external Home
Assistant socket revocation, global logout across replicas, recovery of account
ownership, or all forms of hosted tenant isolation. Spoken-code login remains
available after obtaining a fresh code from the robot.

## Signing keys and upgrades

Configure a private random portal signing key when sessions must survive API
restarts or work across replicas. Generate at least 32 random bytes with a
trusted local generator and retain the result privately in the installation's
environment:

```text
OPENJIBO_PORTAL_SESSION_SIGNING_KEY=YOUR_PRIVATE_RANDOM_KEY
```

The configuration equivalent is `OpenJibo:Portal:SessionSigningKey`. Explicit
signing-key configuration takes precedence over the status-password fallback.
The existing private `OpenJibo:Portal:StatusPassword` / 
`OPENJIBO_PORTAL_STATUS_PASSWORD` remains a fallback when no signing key is set.
Administrator login still requires that separately configured status password;
setting a signing key does not enable passwordless administrator login.

When neither setting is configured, each API process generates its own random
signing key. It is not a shared development key and is not persisted. Sessions
then require login after restart and do not move between replicas. Preserve the
configured private key across replacement/recovery when that continuity is
required. Do not publish it in support reports or reuse another installation's
key. Changing a signing key invalidates existing portal sessions.

The session-purpose update rejects tokens issued by older runtime versions,
including old administrator and spoken-code tokens. **Sign in again once after
upgrading.** This does not rotate user-data encryption keys, change stored
credentials or reset robot/account data. Old signed token formats are not
silently elevated into a new session purpose.

## Verification scope

Regression tests cover old-owner reads and integration deletion, account-only
access without topology creation, normal pairing/selection, spoken-code access
before and after ownership changes, administrator login and a robot named
`portal-admin`. Signing tests cover independent unconfigured instances, explicit
shared/different keys and rejection of the old token shape. A PostgreSQL test
uses two application stores and two signed-session services to recheck the
committed ownership link across instances without relying on a local blacklist.

These source tests do not transfer their results to an older published image.
Use a release explicitly built from the session-purpose/ownership-guard changes;
earlier digest-pinned previews remain unchanged.
