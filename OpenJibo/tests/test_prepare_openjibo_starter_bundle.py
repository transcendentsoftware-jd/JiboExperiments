"""Contract tests for the checkout-free starter archive."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock
import zipfile


REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "OpenJibo/scripts/cloud/prepare-openjibo-starter-bundle.py"
spec = importlib.util.spec_from_file_location("starter_bundle", SCRIPT)
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)
IMAGE = "registry.example/openjibo/cloud@sha256:" + "a" * 64


class StarterBundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.repo = self.base / "source"
        self.repo.mkdir()
        for source, _ in bundle.SOURCES:
            target = self.repo / source
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / source, target)

    def test_archive_is_deterministic_complete_and_excludes_secrets(self):
        (self.repo / "OpenJibo/.env").write_text("PRIVATE_SHOULD_NOT_APPEAR=123\n")
        (self.repo / "OpenJibo/untracked-secret.txt").write_text("PRIVATE_SHOULD_NOT_APPEAR=456\n")
        first, second = self.base / "first.zip", self.base / "second.zip"
        bundle.write_bundle(self.repo, IMAGE, first)
        bundle.write_bundle(self.repo, IMAGE, second)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertNotIn(b"PRIVATE_SHOULD_NOT_APPEAR", first.read_bytes())
        with zipfile.ZipFile(first) as archive:
            names = archive.namelist()
            self.assertEqual(names, sorted(names))
            self.assertNotIn(".env", names)
            self.assertNotIn("untracked-secret.txt", names)
            self.assertEqual(set(names), {target for _, target in bundle.SOURCES} | {"README.md", "start.sh", "Start.ps1", "MANIFEST.json", "CPU-PROFILE.json"})
            manifest = json.loads(archive.read("MANIFEST.json"))
            self.assertEqual(manifest["runtime_image"], IMAGE)
            self.assertEqual(manifest["sha256"], {
                name: hashlib.sha256(archive.read(name)).hexdigest()
                for name in names if name != "MANIFEST.json"
            })
            self.assertTrue(all(info.date_time == (1980, 1, 1, 0, 0, 0) for info in archive.infolist()))
            for info in archive.infolist():
                self.assertEqual((info.external_attr >> 16) & 0o777,
                                 0o755 if info.filename.endswith('.sh') else 0o644)
                self.assertNotIn(b'\r', archive.read(info.filename))
            compose = archive.read("docker-compose.yml").decode()
            self.assertEqual(compose.count(f"image: {IMAGE}"), 2)
            self.assertNotIn("build:", compose)
            self.assertNotIn("Dockerfile", compose)
            self.assertNotIn('"5432:5432"', compose)
            self.assertIn('"127.0.0.1:8080:8080"', compose)
            self.assertEqual(compose.count("${OPENJIBO_POSTGRES_PASSWORD:?"), 3)
            self.assertIn(IMAGE.encode(), archive.read("start.sh"))
            self.assertIn(IMAGE.encode(), archive.read("Start.ps1"))

    def test_rejects_invalid_digest(self):
        for image in ("openjibo:latest", "registry.example/repo@sha256:" + "A" * 64, "repo@sha256:" + "a" * 64):
            with self.subTest(image=image), self.assertRaises(ValueError):
                bundle.build_payload(self.repo, image)

    def test_cpu_profile_is_explicit_and_checksum_covered(self):
        payload = bundle.build_payload(self.repo, IMAGE, "avx2")
        profile = json.loads(payload["CPU-PROFILE.json"])
        self.assertEqual(profile["profile"], "avx2")
        self.assertEqual(profile["required_x86_flags"], ["avx", "avx2", "bmi2", "f16c", "fma", "sse4_2"])
        manifest = json.loads(payload["MANIFEST.json"])
        self.assertEqual(manifest["sha256"]["CPU-PROFILE.json"], hashlib.sha256(payload["CPU-PROFILE.json"]).hexdigest())
        with self.assertRaises(ValueError):
            bundle.build_payload(self.repo, IMAGE, "native")

    def test_refuses_existing_output(self):
        output = self.base / "bundle.zip"
        output.write_bytes(b"keep me")
        with self.assertRaises(FileExistsError):
            bundle.write_bundle(self.repo, IMAGE, output)
        self.assertEqual(output.read_bytes(), b"keep me")

    def test_changed_compose_contract_fails(self):
        compose = self.repo / "OpenJibo/docker-compose.yml"
        compose.write_text(compose.read_text().replace("      context: .", "      context: elsewhere", 1))
        with self.assertRaisesRegex(ValueError, "Source Compose changed"):
            bundle.build_payload(self.repo, IMAGE)

    def test_unknown_compose_dependency_fails_without_output(self):
        compose = self.repo / "OpenJibo/docker-compose.yml"
        compose.write_text(compose.read_text() + "\ninclude: ../private.yml\n")
        output = self.base / "rejected.zip"
        with self.assertRaisesRegex(ValueError, "Source Compose changed"):
            bundle.write_bundle(self.repo, IMAGE, output)
        self.assertFalse(output.exists())

    def test_source_symlink_is_rejected(self):
        source = self.repo / "LICENSE"
        source.unlink()
        outside = self.base / "private-license"
        outside.write_text("not an input")
        try:
            source.symlink_to(outside)
        except OSError:
            self.skipTest("Host does not permit unprivileged symlinks")
        with self.assertRaisesRegex(ValueError, "Symlink source"):
            bundle.build_payload(self.repo, IMAGE)

    def test_concurrent_output_is_preserved_and_staging_removed(self):
        output = self.base / "race.zip"
        real_link = os.link

        def competing_link(source, destination):
            Path(destination).write_bytes(b"concurrent winner")
            return real_link(source, destination)

        with mock.patch.object(bundle.os, "link", side_effect=competing_link):
            with self.assertRaises(FileExistsError):
                bundle.write_bundle(self.repo, IMAGE, output)
        self.assertEqual(output.read_bytes(), b"concurrent winner")
        self.assertEqual(list(self.base.glob(".openjibo-starter-*.zip")), [])

    @unittest.skipUnless(os.environ.get("OPENJIBO_TEST_COMPOSE_CONFIG") == "1", "opt-in Docker Compose syntax check")
    def test_generated_compose_preflight_requires_password(self):
        if not shutil.which("docker"):
            self.skipTest("Docker CLI unavailable")
        extracted = self.base / "extracted"
        extracted.mkdir()
        payload = bundle.build_payload(self.repo, IMAGE)
        for name, data in payload.items():
            destination = extracted / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        env = {key: value for key, value in os.environ.items()
               if not key.startswith("COMPOSE_") and not key.startswith("OPENJIBO_")}
        compose_env = extracted / ".env"
        compose_env.write_text("OPENJIBO_POSTGRES_PASSWORD=strong-test-only-password\n")
        result = subprocess.run(["docker", "compose", "config", "--quiet"], cwd=extracted, env=env,
                                capture_output=True, text=True, timeout=30, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        compose_env.write_text("")
        result = subprocess.run(["docker", "compose", "config", "--quiet"], cwd=extracted, env=env,
                                capture_output=True, text=True, timeout=30, check=False)
        self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
