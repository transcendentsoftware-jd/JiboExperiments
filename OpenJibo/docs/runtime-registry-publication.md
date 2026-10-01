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

This publication workflow is prepared but has not been run. First run requires
the environment credential setup. A partial matrix failure can leave a published
preview for one profile; do not list it as a complete release. Retain the successful
registry digest and repair/retry under a new run tag rather than assuming atomic
multi-profile publication.
