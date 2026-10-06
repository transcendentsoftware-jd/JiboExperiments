# Starter ZIP provenance foundation

The preview publisher now attests each verified starter ZIP, then verifies the
signature before uploading release evidence. This applies to future runs only.
The existing `runtime-preview-36863178102` ZIPs remain checksum-only; their runtime
image provenance was verified separately. Do not overwrite that release or
describe it as having signed ZIP provenance.

`verify-starter-provenance.py` checks the archive contract and independently
trusted SHA-256, then invokes GitHub CLI cryptographic verification against the
fixed `transcendentsoftware-jd/JiboExperiments` repository and
`openjibo-runtime-preview-publish.yml` signer workflow, exact source commit,
GitHub Actions OIDC issuer and SLSA provenance predicate. Self-hosted runner
attestations are rejected. Both checks use the same bounded byte snapshot.
Any failure stops verification; there is no checksum-only fallback.

Inspect the verifier from a trusted checkout first. For a future attested ZIP,
use Python 3 and a GitHub CLI supporting these attestation verification flags:

```bash
python3 -B OpenJibo/scripts/cloud/verify-starter-provenance.py \
  --bundle /path/to/starter-portable-preview.zip \
  --expected-sha256 TRUSTED_FULL_ARCHIVE_SHA256 \
  --source-commit TRUSTED_FULL_SOURCE_COMMIT \
  --attestation /path/to/starter-portable-attestation.json
```

Omitting `--attestation` queries GitHub for the signature. Supplying a bundle
does not guarantee fully offline verification: trust roots or services may still
require network access. Checksums and source identity must come from a trusted
release record, not solely from adjacent untrusted download files.

Offline regression tests cover policy enforcement and failure behavior using
mocked CLI results. Actual positive cryptographic execution remains pending the
first new publisher run; no publication or runtime deployment is implied by
these tests. Signed builder identity is not stable-channel freshness, rollback
protection, payload license approval, SBOM certification or physical-robot
acceptance. Integration with the preparer's release selection is still pending.
