# Immutable published starter fixtures

These are exact ZIP bytes downloaded and independently signature/hash verified
from the two named experimental releases. They contain templates, scripts and
documentation, never initialized environments, private keys, database dumps or
volume backups. Their historical documentation is a test input, not current
operator guidance; use the current recovery guide for actual installations.

Published-preparer tests compare the complete ZIP SHA-256 with the reviewed
release pin before testing mandatory provenance verification. Native-launcher
tests use the real manifest and CPU profile to exercise the existing host gate.
No archive/manifest checksum pin is overridden for these fixtures.

Current source and bundled documentation may evolve without pretending that
those changes were present in an older signed ZIP. Byte-for-byte reconstruction
from the corresponding reviewed source and real signature verification remain
part of publication/independent-release checks, separately from these offline
contract tests. Do not regenerate these files from a newer checkout.

| Version | Profile | ZIP SHA-256 |
| --- | --- | --- |
| runtime-preview-41729015780 | portable | `66febd2c39e0e4ec9c923c086eaf6cfee064020f3a662033d27c8119ae972709` |
| runtime-preview-41729015780 | avx2 | `b8a0ece11ecdd393af0c7522dcf10e5daf9a4e9a4d4fb9dfd3922ec5bd78be7f` |
| runtime-preview-41757874916 | portable | `a23c0b1f19619e29dc8e669a0ff3dbd9591fb2e85e466163c64cbac646ca5816` |
| runtime-preview-41757874916 | avx2 | `9baf78173db0d1c98adc255a932454aedf1eac10bc03a8be9e8bf493c55c3e65` |
