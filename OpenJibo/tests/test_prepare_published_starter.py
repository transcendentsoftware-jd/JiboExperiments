"""Safety checks for explicit targeted preview preparation, without network."""
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


installer = load("published_preparer", ROOT / "OpenJibo/scripts/cloud/prepare-published-starter.py")
builder = load("published_builder", ROOT / "OpenJibo/scripts/cloud/prepare-openjibo-starter-bundle.py")


class PublishedStarterTests(unittest.TestCase):
    def test_reject_untrusted_redirects(self):
        for url in ("http://github.com/file", "https://evil.example/file", "https://github.com.evil.example/file", "https://user@github.com/file", "https://github.com:8443/file"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                installer.trusted_url(url)

    def test_accept_release_hosts(self):
        for url in (installer.URL, "https://release-assets.githubusercontent.com/file?token=example"):
            self.assertEqual(installer.trusted_url(url), url)

    def test_bounded_download(self):
        for data in (b"", b"x" * (installer.LIMIT + 1)):
            with self.assertRaises(ValueError):
                installer.read_bounded(io.BytesIO(data))

    def test_corrupt_bundle_creates_nothing(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "new"
            with self.assertRaises(ValueError):
                installer.prepare(b"corrupt", target)
            self.assertFalse(target.exists())

    def test_verified_bundle_prepares_but_never_initializes_secrets(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.IMAGE, bundle)
            data = bundle.read_bytes()
            with patch.object(installer, "SHA256", hashlib.sha256(data).hexdigest()):
                target = base / "new"
                result = installer.prepare(data, target)
                self.assertFalse(result["docker_started"])
                self.assertFalse(result["provenance_verified"])
                self.assertEqual(result["verification_policy"], "reviewed-preview-checksum-only")
                self.assertTrue((target / "docker-compose.yml").is_file())
                self.assertFalse((target / ".env").exists())
                if sys.platform != "win32":
                    self.assertEqual((target / "scripts/cloud/postgres-init").stat().st_mode & 0o777, 0o755)
                    self.assertEqual((target / "scripts/cloud/postgres-init/01-create-databases.sh").stat().st_mode & 0o777, 0o755)
                with self.assertRaises(ValueError):
                    installer.prepare(data, target)

    def test_wrong_profile_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.IMAGE, bundle, cpu_profile="avx2")
            data = bundle.read_bytes()
            with patch.object(installer, "SHA256", hashlib.sha256(data).hexdigest()):
                with self.assertRaises(ValueError):
                    installer.prepare(data, base / "new")
            self.assertFalse((base / "new").exists())

    def test_provenance_failure_creates_no_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.IMAGE, bundle)
            data = bundle.read_bytes()
            target = base / "new"
            with patch.object(installer, "SHA256", hashlib.sha256(data).hexdigest()), patch.object(installer, "verify_provenance", side_effect=ValueError("missing signature")) as verify:
                with self.assertRaises(ValueError):
                    installer.prepare(data, target, require_provenance=True)
                verify.assert_called_once_with(data, None, installer.VERSION)
            self.assertFalse(target.exists())

    def test_signed_policy_verifies_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.IMAGE, bundle)
            data = bundle.read_bytes()
            target = base / "new"
            proof = base / "proof.json"
            def verify(snapshot, attestation, version):
                self.assertFalse(target.exists())
                self.assertEqual(snapshot, data)
                self.assertEqual(attestation, proof)
                self.assertEqual(version, installer.VERSION)
            with patch.object(installer, "SHA256", hashlib.sha256(data).hexdigest()), patch.object(installer, "verify_provenance", side_effect=verify):
                result = installer.prepare(data, target, require_provenance=True, attestation=proof)
            self.assertTrue(result["provenance_verified"])
            self.assertEqual(result["verification_policy"], "signed-provenance")
            self.assertFalse((target / ".env").exists())

    def test_attestation_cannot_be_silently_ignored(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "new"
            with self.assertRaises(ValueError):
                installer.prepare(b"invalid", target, attestation=Path(temp) / "proof.json")
            self.assertFalse(target.exists())

    def test_signed_release_always_checks_provenance(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.SIGNED_IMAGE, bundle)
            data = bundle.read_bytes()
            target = base / "new"
            with patch.object(installer, "SIGNED_SHA256", hashlib.sha256(data).hexdigest()), patch.object(installer, "verify_provenance") as verify:
                result = installer.prepare(data, target, version=installer.SIGNED_VERSION)
                verify.assert_called_once_with(data, None, installer.SIGNED_VERSION)
            self.assertTrue(result["provenance_verified"])
            self.assertEqual(result["runtime_image"], installer.SIGNED_IMAGE)

    def test_signed_release_signature_failure_cannot_downgrade(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            bundle = base / "fixture.zip"
            builder.write_bundle(ROOT, installer.SIGNED_IMAGE, bundle)
            data = bundle.read_bytes()
            target = base / "new"
            with patch.object(installer, "SIGNED_SHA256", hashlib.sha256(data).hexdigest()), patch.object(installer, "verify_provenance", side_effect=ValueError("signature failed")):
                with self.assertRaises(ValueError):
                    installer.prepare(data, target, require_provenance=False, version=installer.SIGNED_VERSION)
            self.assertFalse(target.exists())

    def test_unknown_versions_cannot_supply_download_pins(self):
        for version in ("latest", "stable", "runtime-preview-999", "../other"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                installer.release_identity(version)


if __name__ == "__main__":
    unittest.main()
