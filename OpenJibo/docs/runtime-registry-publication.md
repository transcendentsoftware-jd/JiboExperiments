# Organization-owned runtime previews

Owner decision, 2026-10-01: Transcendent-Software-LLC owns the images at
`ghcr.io/transcendent-software-llc/openjibo-runtime`. JiboExperiments remains the
shared runtime source repository. OpenJiboCloud is the commercial control plane;
it does not become a second runtime implementation or release source.

`openjibo-runtime-preview-publish.yml` is a manual main-only workflow. It requires
passing runtime CI for its exact source commit, builds portable and AVX2 variants,
checks compiled flags/model checksum/executable startup, and publishes uniquely
named preview tags. It records the registry digest, creates GitHub build-provenance
attestations for that digest, and packages/verifies starters against that reference.
Neither stable nor latest is updated. It does not deploy or change robots.

## Required setup

In `transcendentsoftware-jd/JiboExperiments`, configure an Actions environment
named `openjibo-runtime-release`, restricted to main with the chosen reviewer
policy. Environment declarations in YAML alone do not set protection rules.

- Environment variable `OPENJIBO_GHCR_PUBLISH_USER`: the GitHub user owning the
  publisher token, with package-creation permission in Transcendent-Software-LLC.
- Environment secret `OPENJIBO_GHCR_PUBLISH_TOKEN`: a classic PAT with
  `write:packages`, authorized for the organization/SSO if required. Avoid adding
  broad repository scopes. Never put the token in source, documentation, or chat.

The personal repository's default GITHUB_TOKEN does not automatically grant
permission to create packages in a different owner namespace. The dedicated
package credential bridges that current layout; an organization-owned release
repository could later eliminate it without moving runtime implementation.

GitHub build provenance is stored with the source repository (the action uses
`push-to-registry: false`). The workflow does not create cosign signatures or an
SBOM. Preview bundles' SHA256SUMS are integrity evidence, not signed download
metadata. Stable release trust, SBOM/license review and actual robot acceptance
remain outstanding. Preview attestations must be verified against the trusted
source repository and workflow identity, not merely accepted from any signer.

After the first publication, review organization package access and visibility.
GitHub defaults new packages to private. Public visibility permits anonymous
downloads; decide visibility before offering download links. Image source labels
describe the personal source repository; cross-owner package access must be
reviewed separately rather than assuming automatic permission inheritance.

The first publication passed both profiles on 2026-10-01:
https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/36863178102.
Published source is `ef8014611bb85139701136b32eb440ab0f253617`.

| Profile | Registry digest | Starter ZIP SHA-256 |
| --- | --- | --- |
| portable | `sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b` | `68f7181b4c50b08c632a482efd9f0a20be3ab3a2ad1de97fe6281a5e320b6a91` |
| avx2 | `sha256:b39887ad412d4eee77bb20ca15fe5ae82bae2024d72487b7947ca37e8426bdf0` | `eaab909b46e1166d08344c7582fb3833fe9bdc596a005e0ce4ee4a8c3bdb6466` |

Both downloaded ZIPs passed the offline verifier against the checksums retained
by the trusted Actions run. `openjibo-runtime-preview-verify.yml` performs a
separate authenticated digest pull, package-owner/visibility inspection, and
provenance verification bound to the publisher workflow and source commit.
It does not deploy images or change package visibility.

Independent verification passed on 2026-10-01:
https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/36863863670.
Both exact digests were pulled and their provenance verified against the source
commit and trusted publishing workflow; revision and CPU-profile labels matched.
The package API confirmed owner `Transcendent-Software-LLC` and visibility
`private`. Anonymous installation is therefore not enabled. Making the preview
public requires a separate owner decision; no visibility was changed here.

The owner subsequently made the package public on 2026-10-01. Anonymous GHCR
token issuance and manifest reads passed for both exact digests, returning HTTP
200 and matching Docker-Content-Digest values. This verifies anonymous manifest
access, not a fresh published-image installation or every layer download.

A partial matrix failure can leave a published
preview for one profile; do not list it as a complete release. Retain the successful
registry digest and repair/retry under a new run tag rather than assuming atomic
multi-profile publication.
