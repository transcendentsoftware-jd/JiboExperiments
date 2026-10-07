"""Mocked fresh-launch contracts; no Docker resources are created by these tests."""
import copy
import hashlib
import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


launcher = load("preview_launcher", ROOT / "OpenJibo/scripts/cloud/launch-published-starter.py")
builder = load("launcher_fixture_builder", ROOT / "OpenJibo/scripts/cloud/prepare-openjibo-starter-bundle.py")
REPORT = {"preflight_passed": True, "issues": [], "compose_version": "5.5.1",
          "docker_server": {"architecture": "x86_64", "memory_total_bytes": 8 * 1024**3},
          "host": {"memory_available_bytes": 4 * 1024**3}}


class LauncherTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.base = Path(temp.name)
        archive = self.base / "fixture.zip"
        builder.write_bundle(ROOT, launcher.IMAGE, archive)
        self.target = self.base / "prepared"
        with zipfile.ZipFile(archive) as source:
            source.extractall(self.target)
        digest = hashlib.sha256((self.target / "MANIFEST.json").read_bytes()).hexdigest()
        self.addCleanup(patch.stopall)
        patch.object(launcher, "MANIFEST_SHA256", digest).start()

    def test_project_and_port_parameters(self):
        for project, port in (("prod", 8084), ("openjibo-a;echo", 8084), ("openjibo-a", 80), ("openjibo-a", 65536)):
            with self.subTest(project=project, port=port), self.assertRaises(ValueError):
                launcher.parameters(project, port)

    def test_file_identity_and_existing_env(self):
        self.assertEqual(launcher.checked_files(self.target), self.target)
        (self.target / ".env").write_text("private")
        with self.assertRaises(ValueError):
            launcher.checked_files(self.target)

    def test_changed_payload_rejected(self):
        (self.target / "docker-compose.yml").write_text("services: {}")
        with self.assertRaises(ValueError):
            launcher.checked_files(self.target)

    def test_changed_manifest_rejected(self):
        (self.target / "MANIFEST.json").write_text("{}")
        with self.assertRaises(ValueError):
            launcher.checked_files(self.target)

    def test_signed_manifest_selects_new_image_without_changing_legacy(self):
        archive = self.base / "signed-fixture.zip"
        builder.write_bundle(ROOT, launcher.SIGNED_IMAGE, archive)
        self.target = self.base / "signed-prepared"
        with zipfile.ZipFile(archive) as source:
            source.extractall(self.target)
        raw = (self.target / "MANIFEST.json").read_bytes()
        with patch.object(launcher, "SIGNED_MANIFEST_SHA256", hashlib.sha256(raw).hexdigest()):
            self.assertEqual(launcher.checked_files(self.target), self.target)
            _, report = self.check_with()
            self.assertEqual(report["version"], "runtime-preview-37540893708")
            self.assertEqual(report["runtime_image"], launcher.SIGNED_IMAGE)
            self.assertFalse(report["docker_started"])
        self.assertEqual(launcher.IMAGE, "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b")

    def test_signed_manifest_cannot_substitute_legacy_image(self):
        raw = (self.target / "MANIFEST.json").read_bytes()
        with patch.object(launcher, "MANIFEST_SHA256", "0" * 64), patch.object(launcher, "SIGNED_MANIFEST_SHA256", hashlib.sha256(raw).hexdigest()):
            with self.assertRaises(ValueError):
                launcher.checked_files(self.target)

    @unittest.skipIf(sys.platform == "win32", "Native Linux symlink contract")
    def test_symlink_rejected(self):
        (self.target / "extra").symlink_to(self.base)
        with self.assertRaises(ValueError):
            launcher.checked_files(self.target)

    def check_with(self, report=REPORT, command_output="", socket_error=None):
        with patch.object(launcher.platform, "system", return_value="Linux"), \
             patch.object(launcher.platform, "machine", return_value="x86_64"), \
             patch.object(launcher.shutil, "which", return_value="/usr/bin/tool"), \
             patch.object(launcher, "collect_preflight", return_value=report), \
             patch.object(launcher.shutil, "disk_usage", return_value=MagicMock(free=10 * 1024**3)), \
             patch.object(launcher, "run", return_value=command_output), \
             patch.object(launcher.socket, "socket") as socket:
            socket.return_value.__enter__.return_value.bind.side_effect = socket_error
            return launcher.check(self.target, "openjibo-launch-test", 8084, ["docker"], {})

    def test_check_is_read_only(self):
        _, report = self.check_with()
        self.assertFalse(report["docker_started"])
        self.assertFalse((self.target / ".env").exists())
        self.assertFalse((self.target / ".preview-launch.lock").exists())

    def test_old_compose_and_bad_arch_refused(self):
        for field in ("compose", "architecture", "memory", "preflight"):
            report = copy.deepcopy(REPORT)
            if field == "compose":
                report["compose_version"] = "2.24.3"
            elif field == "architecture":
                report["docker_server"]["architecture"] = "aarch64"
            elif field == "memory":
                report["host"]["memory_available_bytes"] = 1
            else:
                report.update(preflight_passed=False, issues=["docker_info_command_failed"])
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.check_with(report)

    def test_existing_resources_and_port_refused(self):
        with self.assertRaises(ValueError):
            self.check_with(command_output="existing-resource")
        with self.assertRaises(OSError):
            self.check_with(socket_error=OSError("port occupied"))

    def test_clean_environment(self):
        with patch.dict(os.environ, {"OPENJIBO_POSTGRES_PASSWORD": "secret", "COMPOSE_FILE": "other", "DOCKER_HOST": "remote", "PATH": "safe"}, clear=True):
            self.assertEqual(launcher.clean_environment(), {"PATH": "safe"})

    def test_explicit_start_generates_private_environment_and_pinned_compose(self):
        calls = []
        def command(args, env, timeout=15):
            calls.append((args, env))
            if args[0] == "bash":
                (self.target / ".env").write_text("generated-private-environment")
                self.assertRegex(env["OPENJIBO_POSTGRES_PASSWORD"], r"^[0-9a-f]{64}$")
            return ""
        with patch.object(launcher, "check", return_value=(self.target, {"docker_started": False})), \
             patch.object(launcher, "run", side_effect=command), \
             patch.object(launcher, "wait_health") as health:
            result = launcher.start(self.target, "openjibo-launch-test", 8084, ["docker"], {})
        self.assertTrue(result["health_passed"])
        health.assert_called_once_with(8084)
        self.assertIn("127.0.0.1:8084:8080", (self.target / "preview-launch.compose.yaml").read_text())
        self.assertIn("--no-build", calls[-1][0])
        self.assertNotIn("OPENJIBO_POSTGRES_PASSWORD", calls[-1][1])
        if sys.platform != "win32":
            self.assertEqual((self.target / ".env").stat().st_mode & 0o777, 0o600)

    def test_failed_start_preserves_environment(self):
        def command(args, env, timeout=15):
            if args[0] == "bash":
                (self.target / ".env").write_text("preserved")
            else:
                raise ValueError("failure")
        with patch.object(launcher, "check", return_value=(self.target, {})), patch.object(launcher, "run", side_effect=command):
            with self.assertRaises(ValueError):
                launcher.start(self.target, "openjibo-launch-test", 8084, ["docker"], {})
        self.assertEqual((self.target / ".env").read_text(), "preserved")
        self.assertTrue((self.target / ".preview-launch.lock").exists())


if __name__ == "__main__":
    unittest.main()
