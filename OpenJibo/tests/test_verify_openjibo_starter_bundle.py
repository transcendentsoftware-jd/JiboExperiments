"""Offline validation of the externally hash-pinned starter ZIP."""

import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile


REPO = Path(__file__).resolve().parents[2]
BUILDER = REPO / "OpenJibo/scripts/cloud/prepare-openjibo-starter-bundle.py"
VERIFIER = REPO / "OpenJibo/scripts/cloud/verify-openjibo-starter-bundle.py"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load_module("starter_builder_for_verification", BUILDER)
verifier = load_module("starter_verifier", VERIFIER)
IMAGE = "registry.example/openjibo/cloud@sha256:" + "a" * 64


class VerifyStarterBundleTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name)
        source = self.base / "source"
        source.mkdir()
        for relative, _ in builder.SOURCES:
            target = source / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / relative, target)
        self.bundle = self.base / "starter.zip"
        builder.write_bundle(source, IMAGE, self.bundle)

    def check(self, path=None, expected=None):
        path = path or self.bundle
        if expected is None:
            expected = hashlib.sha256(path.read_bytes()).hexdigest()
        return subprocess.run(
            [sys.executable, str(VERIFIER), "--bundle", str(path),
             "--expected-sha256", expected],
            capture_output=True, text=True, check=False,
        )

    def rewrite(self, change):
        with zipfile.ZipFile(self.bundle) as original:
            entries = [(info.filename, original.read(info)) for info in original.infolist()]
        entries = change(entries)
        output = self.base / "modified.zip"
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(output, "w") as archive:
                for name, data in entries:
                    info = zipfile.ZipInfo(name)
                    info.create_system = 3
                    info.external_attr = (stat.S_IFREG | (0o755 if name.endswith(".sh") else 0o644)) << 16
                    archive.writestr(info, data)
        return output

    def test_generated_bundle_verifies_and_reports_json(self):
        result = self.check()
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["runtime_image"], IMAGE)
        self.assertEqual(report["archive_sha256"], hashlib.sha256(self.bundle.read_bytes()).hexdigest())
        self.assertTrue(report["verified_against_external_sha256"])
        self.assertEqual(report["declared_cpu_profile"], "portable")

    def test_legacy_bundle_has_no_cpu_claim(self):
        def legacy(entries):
            result = []
            for name, data in entries:
                if name == "CPU-PROFILE.json":
                    continue
                if name == "MANIFEST.json":
                    manifest = json.loads(data)
                    manifest["sha256"].pop("CPU-PROFILE.json")
                    data = json.dumps(manifest).encode()
                result.append((name, data))
            return result
        report = self.check(self.rewrite(legacy))
        self.assertEqual(report.returncode, 0, report.stderr)
        self.assertIsNone(json.loads(report.stdout)["declared_cpu_profile"])

    def test_cpu_declaration_rejects_invalid_requirements_even_with_updated_hash(self):
        def invalid(entries):
            values = dict(entries)
            profile = json.loads(values["CPU-PROFILE.json"])
            profile.update(profile="avx2", required_x86_flags=[])
            values["CPU-PROFILE.json"] = json.dumps(profile).encode()
            manifest = json.loads(values["MANIFEST.json"])
            manifest["sha256"]["CPU-PROFILE.json"] = hashlib.sha256(values["CPU-PROFILE.json"]).hexdigest()
            values["MANIFEST.json"] = json.dumps(manifest).encode()
            return list(values.items())
        result = self.check(self.rewrite(invalid))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("CPU profile", result.stderr)

    def test_hash_checked_before_zip_parsing(self):
        invalid = self.base / "invalid.zip"
        invalid.write_bytes(b"this is not a zip")
        result = self.check(invalid, "0" * 64)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SHA-256", result.stderr)
        self.assertNotIn("zip", result.stderr.lower())
        self.assertEqual(result.stdout, "")

    def test_rejects_missing_or_malformed_expected_hash(self):
        missing = subprocess.run(
            [sys.executable, str(VERIFIER), "--bundle", str(self.bundle)],
            capture_output=True, text=True, check=False,
        )
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn("--expected-sha256", missing.stderr)
        self.assertEqual(missing.stdout, "")
        malformed = self.check(expected="A" * 64)
        self.assertNotEqual(malformed.returncode, 0)
        self.assertIn("64 lowercase hex", malformed.stderr)
        self.assertEqual(malformed.stdout, "")

    def test_rejects_archive_over_size_cap(self):
        output = self.base / "oversize.zip"
        output.write_bytes(b"x" * (verifier.MAX_ARCHIVE_BYTES + 1))
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("2 MiB limit", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_duplicate_and_unknown_paths(self):
        for label, change in (
            ("duplicate", lambda entries: entries + [entries[0]]),
            ("traversal", lambda entries: entries + [("../secret", b"x")]),
            ("backslash", lambda entries: entries + [("scripts\\secret", b"x")]),
            ("absolute", lambda entries: entries + [("/secret", b"x")]),
        ):
            with self.subTest(label=label):
                output = self.rewrite(change)
                result = self.check(output)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")

    def test_rejects_symlink_and_oversized_member(self):
        for kind in ("symlink", "oversized"):
            with self.subTest(kind=kind):
                output = self.base / f"{kind}.zip"
                with zipfile.ZipFile(self.bundle) as original, zipfile.ZipFile(output, "w") as archive:
                    for old in original.infolist():
                        info = zipfile.ZipInfo(old.filename)
                        info.create_system = 3
                        mode = stat.S_IFLNK if kind == "symlink" and old.filename == "start.sh" else stat.S_IFREG
                        info.external_attr = (mode | (0o755 if old.filename.endswith(".sh") else 0o644)) << 16
                        data = original.read(old)
                        if kind == "oversized" and old.filename == "README.md":
                            data = b"x" * (verifier.MAX_MEMBER_BYTES + 1)
                        archive.writestr(info, data)
                result = self.check(output)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")

    def test_rejects_unexpected_permissions(self):
        output = self.base / "world-writable.zip"
        with zipfile.ZipFile(self.bundle) as original, zipfile.ZipFile(output, "w") as archive:
            for old in original.infolist():
                info = zipfile.ZipInfo(old.filename)
                info.create_system = 3
                permission = 0o666 if old.filename == "README.md" else (0o755 if old.filename.endswith(".sh") else 0o644)
                info.external_attr = (stat.S_IFREG | permission) << 16
                archive.writestr(info, original.read(old))
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("permissions", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_nul_truncated_filename(self):
        output = self.rewrite(lambda entries: [
            ("README.mdX" if name == "README.md" else name, data)
            for name, data in entries
        ])
        raw = output.read_bytes()
        self.assertEqual(raw.count(b"README.mdX"), 2)
        output.write_bytes(raw.replace(b"README.mdX", b"README.md\x00"))
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Truncated archive filename", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_compressed_member(self):
        output = self.base / "compressed.zip"
        with zipfile.ZipFile(self.bundle) as original, zipfile.ZipFile(output, "w") as archive:
            for old in original.infolist():
                info = zipfile.ZipInfo(old.filename)
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | (0o755 if old.filename.endswith(".sh") else 0o644)) << 16
                info.compress_type = zipfile.ZIP_DEFLATED if old.filename == "README.md" else zipfile.ZIP_STORED
                archive.writestr(info, original.read(old))
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Compressed member", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_payload_tamper_with_matching_archive_hash(self):
        output = self.rewrite(lambda entries: [
            (name, data + b"tampered" if name == "README.md" else data)
            for name, data in entries
        ])
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Checksum mismatch", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_manifest_schema_coverage_and_checksums(self):
        def mutate(entries, change):
            result = []
            for name, data in entries:
                if name == "MANIFEST.json":
                    manifest = json.loads(data)
                    change(manifest)
                    data = json.dumps(manifest).encode()
                result.append((name, data))
            return result

        changes = (
            lambda manifest: manifest.update(schema=True),
            lambda manifest: manifest["sha256"].pop("README.md"),
            lambda manifest: manifest["sha256"].update({"README.md": "0" * 64}),
            lambda manifest: manifest.update(runtime_image="openjibo:latest"),
        )
        for change in changes:
            with self.subTest(change=change):
                output = self.rewrite(lambda entries: mutate(entries, change))
                result = self.check(output)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")

    def test_rejects_duplicate_manifest_keys(self):
        output = self.rewrite(lambda entries: [
            (name, data.replace(b'"schema": 1,', b'"schema": 1, "schema": 1,')
             if name == "MANIFEST.json" else data)
            for name, data in entries
        ])
        result = self.check(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Duplicate manifest key", result.stderr)
        self.assertEqual(result.stdout, "")

    def test_rejects_valid_checksums_with_mismatched_entrypoint_image(self):
        def change(entries):
            changed = []
            for name, data in entries:
                if name == "start.sh":
                    data = data.replace(IMAGE.encode(), b"registry.example/other/cloud@sha256:" + b"b" * 64)
                elif name == "MANIFEST.json":
                    manifest = json.loads(data)
                    start = next(content for member, content in entries if member == "start.sh")
                    replacement = start.replace(IMAGE.encode(), b"registry.example/other/cloud@sha256:" + b"b" * 64)
                    manifest["sha256"]["start.sh"] = hashlib.sha256(replacement).hexdigest()
                    data = json.dumps(manifest).encode()
                changed.append((name, data))
            return changed

        result = self.check(self.rewrite(change))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Entrypoint runtime image", result.stderr)


if __name__ == "__main__":
    unittest.main()
