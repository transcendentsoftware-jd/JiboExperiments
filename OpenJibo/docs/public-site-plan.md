# Public Site Plan

## Goal

Stand up a small public site and web app on `openjibo.com` that makes the project understandable in a few minutes and gives the owner-facing web surface a real home.

`jiborevived.com` remains the community-maintained Jibo Revival Group hub and status space. `openjibo.com` is the Open Jibo showcase, account entry surface, and hosted-cloud entry point.

## Current Status (`2026-10-12`)

The neutral site is public at **https://openjibo.com/** with HTTPS enforced.
Republishing temporarily reset Pages to branch-root publishing; the intended
Actions source was restored and verified in run `38251443557`. Keep GitHub
Actions as the Pages source; do not publish the repository README from `/`.

### Human-first site milestone

The owner requested a welcoming, visually appealing site and genuinely easy
instructions—not an engineering progress report presented as a landing page.
The current usability pass adds:

- Separate first steps for robot owners seeking community help and Linux
  tinkerers testing the server. The server installer does not convert a robot.
- Warm, readable typography, a lightweight inline robot illustration, clear
  hosting availability labels, responsive layouts and accessible FAQs.
- A four-step preview guide: prerequisites, verified preparation, explicit
  launch, then a simple health checkpoint. It keeps the existing signed release
  and all safety/acceptance limitations; it does not promise a stable installer.
- Copy controls with a manual fallback, clear expected results, fresh timestamped
  test names, and non-destructive stop instructions. Technical evidence and
  older releases remain accessible through expandable details.
- Tests for audience boundaries, valid links/copy targets, preserved release
  pins, copy-control behavior, and fresh-name/data-preservation instructions.

The Sites design guidance informed the audience-first structure and progressive
disclosure. Hosting stays on GitHub Pages; no Sites hosting or cloud/runtime
deployment is introduced. Interactive visual/keyboard QA is still outstanding:
the browser helper failed at sandbox startup and the headless attempt was
interrupted. Do not report a screenshot or visual acceptance that did not occur.

### Next usability legs

1. Inspect the published desktop/mobile experience and observe a newcomer using
   the guide without coaching; repair each unclear step rather than merely adding
   more technical warnings. Test the revised commands on a fresh supported host.
2. Provide one guided, version-targeted download/preflight flow so the user does
   not need a source checkout. Keep provenance checks, explicit launch approval,
   platform checks, backups and recovery—no blind download-and-run pipe.
3. Add easy, tested prerequisites for each supported operating system. Windows,
   ARM64, automatic upgrades and stable-channel selection must stay unavailable
   until their own acceptance gates pass.
4. Add provider-neutral robot conversion/onboarding guidance as JiboAutoMod gains
   the orchestrated conversion flow; separately document hybrid/provider starters
   and eventually official robot builds/OTA. Do not imply these are available now.
5. Link OpenJibo Cloud signup only after production auth, recovery, hostname and
   closed-preview gates pass. Website progress does not approve that deployment.

## Earlier publication milestones

Update, 2026-10-11: the owner approved recommending the guarded-source signed
experimental preview `runtime-preview-38165346370`. The site source now pins both
ZIP checksums and signature links to that release, with portable preparation/
launch examples and explicitly gated AVX2 instructions. Both profiles have fresh
native Linux synthetic installation, speech, restart and backup/restore evidence.
The [acceptance record](guarded-source-preview-publication-20261011.md) retains
exact identities and limits. Earlier releases remain linked and unchanged.
This is a recommendation for experimental testing, not stable promotion or
clearance of the remaining license/notice/physical-robot gates. Public-site
workflow publication is separate from runtime deployment; no runtime deploy or
custom-domain cutover is authorized by this update.

The owner subsequently verified and approved the apex/`www` domain cutover;
its DNS records now point to Pages and HTTPS routing works. The
[domain runbook and execution record](public-site-domain-cutover.md) preserves
the original routing snapshot, executed checks and separate managed-cloud gate.

Status: neutral preview published at
https://transcendentsoftware-jd.github.io/JiboExperiments/.
First publication passed:
https://github.com/transcendentsoftware-jd/JiboExperiments/actions/runs/36936001289.
The durable targeted bundles are an explicit prerelease at
https://github.com/transcendentsoftware-jd/JiboExperiments/releases/tag/runtime-preview-36863178102.
The original project-URL publication is historical; the apex domain is now
configured. Visual/keyboard acceptance of this redesign and its new guide flow
still need tester evidence; earlier package acceptance is recorded separately.

The repository now contains a neutral static overview and preview downloads/test
page in `src/OpenJibo.Site`. It compares all three hosting profiles, links the
revival community, and labels unimplemented starters and release gates clearly.
`openjibo-public-site.yml` publishes only this allowlisted directory through a
manual main-only GitHub Pages publication. The domain cutover is complete;
visual acceptance and fuller contact/documentation journeys remain outstanding.
Commercial membership belongs on each provider's own clearly labeled surface.

The [official distribution plan](official-distribution-plan.md) adds versioned
downloads, isolated/hybrid/managed starter packages, host adapters and verified
updates/mirrors to this site work. Promote the revival group and its existing
community hub; do not replace or conflate that hub with a commercial provider.
The preview catalog labels compilation, registry/provenance and offline ZIP
verification separately from pending published-bundle and physical-robot
acceptance. Do not promote previews as stable supported downloads before the
full package/platform acceptance matrix passes.

## First Version Content

- project overview
- current status
- links to source repositories
- roadmap / long-range plan
- links to device bootstrap docs
- explanation of the hosted-cloud direction
- owner/account entry flows that can hand off into auth or onboarding
- a clear path into the managed cloud experience when applicable
- contribution/contact or waitlist path

## Hosting Choice And Onboarding Split

The public site should make the hosted-cloud path explicit instead of hiding it inside one general account page.

Account/onboarding entry here means a clearly labeled handoff to the selected
provider or local setup, not an already-existing neutral identity service.
Reading the site, comparing options and accessing self-hosted documentation or
downloads must not require a managed-service login.

Recommended host split:

- `openjibo.com`: showcase, docs, account entry, and owner-facing overview
- `auth.openjibo.com`: only if a shared neutral identity authority is intentionally separated; do not assume one commercial provider owns ecosystem identity
- `cloud.openjibo.com`: Transcendent Software LLC's clearly labeled paid managed service
- other admitted provider domains: their own terms, status, support, privacy, and onboarding surfaces

Recommended onboarding flow:

1. Start from `openjibo.com` or the robot conversion/onboarding entry.
2. Compare managed OpenJibo Cloud, other community providers, owner-managed hosting, self-hosted hybrid, and self-hosted isolated choices.
3. Explicitly select a provider or self-hosted target.
4. If the target requires signup/payment or other authorization, use a short-lived signed handoff to that provider.
5. The provider returns a signed success/failure result bound to the onboarding session.
6. On success, onboarding writes and verifies the selected provider target.
7. On failure, onboarding stops safely and preserves retry, provider-switch, export, and self-hosted recovery choices.
8. On cancellation/revocation, the provider applies its policy without silently moving the robot elsewhere or deleting owner data.

## Hosting Choices Page

Compare every option on the same dimensions:

- operator and source repository
- price
- setup difficulty
- maintenance responsibility
- backups and restore behavior
- privacy/data-location summary
- support and public status
- supported device/conversion requirements
- shared identity/sync dependency
- export, provider-switch, and recovery path

The Transcendent Software service can be featured, but it must be labeled as commercial and should not hide self-hosting or future admitted providers.

## Contact

Route a minimal form by topic: platform/community, compatibility/conversion, provider application, managed-service account/sales, managed-service support, security/privacy, and partnership/press. Collect only what is necessary, disclose the recipient and retention, rate-limit abuse, and keep vulnerability reports out of the general form.

## What The Site Should Not Pretend Yet

- zero-touch recovery
- complete parity with the original cloud
- public production readiness before device validation is repeatable
- that `neohub.openjibo.com` needs a separate public web presence unless routing evidence proves it

## Initial Repo Asset

A simple static site scaffold lives in [src/OpenJibo.Site](/OpenJibo/src/OpenJibo.Site).
