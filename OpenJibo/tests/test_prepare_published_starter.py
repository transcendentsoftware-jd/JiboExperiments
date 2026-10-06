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


if __name__ == "__main__":
    unittest.main()
