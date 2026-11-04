# Try the offline conversion review

Use the [November 4 offline-review prerelease](https://github.com/Jibo-Revival-Group/JiboAutoMod/releases/tag/offline-review-2026.11.04-preview.1)
to try the guided review on Windows, Linux or macOS. This is a separate download
from the self-hosted server. The bundled demo needs Python 3.9 or newer and a
writable folder; it needs no robot, Docker, USB driver, Python packages or network
connection after download. Python must already be installed.

## Download and check

Download `OpenJibo-Offline-Review.zip`, its `.sha256` companion and
`release-manifest.json` from that exact release. The ZIP is 4,645,332 bytes and
its SHA-256 is:

```text
dc10c6c2ef4877b8833c94279164bcbc9044591aba928a9e1878aef49fde8d60
```

Check the downloaded ZIP before extracting:

```powershell
# Windows PowerShell, in the download folder
Get-FileHash .\OpenJibo-Offline-Review.zip -Algorithm SHA256
```

```bash
# Linux
sha256sum OpenJibo-Offline-Review.zip
# macOS
shasum -a 256 OpenJibo-Offline-Review.zip
```

Compare the complete hash with the published checksum and the value above.
These are integrity checks, not a digital signature or proof of trustworthy
acquisition. The release manifest binds the 52 payload files to source
`2554f3ecde615c87e420953aa0489120cd51836b`. Keep older release evidence with its
own archive; these values describe this release only.

## Open the demo

Extract the entire ZIP to a new writable local folder. On Windows, double-click
`Run-Demo.cmd`, or run this from the extracted folder:

```powershell
py -3 -I -B demo_review.py --open
```

On Linux or macOS:

```bash
python3 -I -B demo_review.py --open
```

The extracted `run-demo.sh` is another entry point (`sh run-demo.sh`). Each run
preserves earlier results and creates a new `demo-review-*` folder. If the
browser does not open, open the local `reviewFile` path printed by the command.
Omit `--open` to generate the review without opening a browser.

This release's synthetic v3 review includes matching primary/backup GPT tables
and a protective MBR, recovery-byte comparisons, firewall sector and endpoint
previews, configuration/region checks and component records. Everything is
generated fake data. A matching result says nothing about a physical robot.

## Why Apply is unavailable

The page assembles evidence and exact proposed changes; it cannot execute them.
No device writer or native payload is included in this package. The selected
target in the demo is an example, not a provider enrollment or a recommended
server address. An assembled review does not establish physical identity,
installed release/mount mapping, filesystem integrity, actual restoration,
effective network/TLS behavior, ownership, bounded apply/readback or rollback.

The [easy-button delivery stages](platform-setup-guide.md#conversion-and-device-preparation)
keep those remaining checks explicit. Starting the
[self-hosted runtime](integration-transfer-runtime-preview-20261103.md) is a
separate server installation and does not convert or enroll a robot.

## Try the separate pre-provisioning intake

From the same folder, use the OS-specific Python command above with
`--oobe-intake --open`:

```powershell
py -3 -I -B demo_review.py --oobe-intake --open
```

```bash
python3 -I -B demo_review.py --oobe-intake --open
```

This separate synthetic demo observes blank credential fields without inventing
a region. It does not prove factory/OOBE state or prepare a robot for apply.
The credentialed combined review still requires its original region evidence.

For your own retained inputs, follow the release's pinned
[one-command review instructions](https://github.com/Jibo-Revival-Group/JiboAutoMod/blob/2554f3ecde615c87e420953aa0489120cd51836b/Docs/conversion-review-bundle.md#one-command-request-and-review),
[recovery-table audit](https://github.com/Jibo-Revival-Group/JiboAutoMod/blob/2554f3ecde615c87e420953aa0489120cd51836b/Docs/gpt-recovery-audit.md)
and [pre-provisioning intake contract](https://github.com/Jibo-Revival-Group/JiboAutoMod/blob/2554f3ecde615c87e420953aa0489120cd51836b/Docs/oobe-storage-intake.md).
Keep real backups/reviews private. Do not copy synthetic partition names,
geometry or example targets as a real robot profile.

## Evidence and limits

The exact public download passed demo, launcher, blank-credential intake,
standalone audit and damaged-backup refusal checks on Windows Python 3.14.6 and
Ubuntu Python 3.12.3, with all 52 payload files unchanged. Cross-platform CI
covered Python 3.9 and current Python on Windows, Linux and macOS. No Mac is
available for native host acceptance; macOS CI does not establish physical
conversion or self-hosting support. These checks did not access a robot.

Physical OOBE and repeat deployment remain separate items in the
[device laboratory plan](oobe-and-repeat-deployment-test-plan.md).
