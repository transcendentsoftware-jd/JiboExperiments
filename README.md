# JiboExperiments

This repository is the working lab for bringing Jibo back.

The active OpenJibo work lives in [OpenJibo/README.md](OpenJibo/README.md), and the long-range story now lives in [OpenJibo/docs/roadmap.md](OpenJibo/docs/roadmap.md).

For a fresh self-hosting experiment, see the tested, opt-in
[`runtime-preview-41768412801` instructions](OpenJibo/docs/integration-transfer-runtime-preview-20261103.md):
native Linux x86-64 supports portable and explicitly selected AVX2; Windows x86-64
supports [portable with a local Docker Desktop Linux engine](OpenJibo/docs/windows-portable-runtime-preview-20261103.md).
Use the current trusted checkout's preparer/launcher and mandatory signature
verification. Calendar and Home Assistant need fresh setup after a robot changes
accounts; same-account re-pairing preserves settings.

This is experimental server installation, not a stable release or completed robot
conversion. Physical OOBE/repeat deployment is pending delivery of the test units;
macOS, ARM64/emulation and Windows AVX2 remain unqualified. The
[download site's older selection](https://transcendentsoftware-jd.github.io/JiboExperiments/downloads.html)
and automatic defaults are unchanged; select the newer version explicitly using
its instructions. [Older preview evidence](OpenJibo/docs/guarded-source-preview-publication-20261011.md)
remains historical. Start with the [platform setup guide](OpenJibo/docs/platform-setup-guide.md)
for hosting choices, recovery and conversion limits.

Try the separate [offline conversion-review demo](OpenJibo/docs/offline-conversion-review-preview-20261104.md)
on Windows, Linux or macOS with Python 3.9+. It uses fake data and requires no
robot or Docker; physical apply remains unavailable.

Repo hygiene: keep generated scratch output, temp captures, and local workspace state out of Git. The `.codex/` tree is ignored by default, and only intentionally shared configuration should be promoted to tracked files.

Start here:

- [OpenJibo overview](OpenJibo/README.md)
- [Roadmap](OpenJibo/docs/roadmap.md)
- [Current cloud truth](OpenJibo/docs/development-plan.md)
- [Working feature queue](OpenJibo/docs/feature-backlog.md)
- [Official site, builds, starters and update plan](OpenJibo/docs/official-distribution-plan.md)
- [Device bootstrap path](OpenJibo/docs/device-bootstrap.md)

This repo intentionally stays a living notebook, not a polished product. That lets us keep the reverse-engineering notes, the hosted cloud, and the post-cloud ideas in one place without pretending they are all equally finished.
