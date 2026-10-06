"""Offline trust-policy contracts; cryptographic execution requires a live attestation."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


verifier = load("starter_provenance", ROOT / "OpenJibo/scripts/cloud/verify-starter-provenance.py")
builder = load("provenance_fixture_builder", ROOT / "OpenJibo/scripts/cloud/prepare-openjibo-starter-bundle.py")


class StarterProvenanceTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.base = Path(temp.name)
        self.bundle = self.base / "starter.zip"
        builder.write_bundle(ROOT, "registry.example/cloud@sha256:" + "a" * 64, self.bundle)
        self.digest = hashlib.sha256(self.bundle.read_bytes()).hexdigest()
        self.commit = "b" * 40

    def test_verification_enforces_fixed_signer_and_source(self):
        def run(command, **kwargs):
            self.assertIn(verifier.REPOSITORY, command)
            self.assertIn(verifier.WORKFLOW, command)
            self.assertIn(self.commit, command)
            self.assertIn("https://slsa.dev/provenance/v1", command)
            self.assertIn("--deny-self-hosted-runners", command)
            self.assertIn("https://token.actions.githubusercontent.com", command)
            self.assertEqual(kwargs["env"]["GH_HOST"], "github.com")
            self.assertNotEqual(Path(command[3]), self.bundle)
            self.assertEqual(Path(command[3]).read_bytes(), self.bundle.read_bytes())
            self.assertFalse(kwargs.get("shell", False))
            return subprocess.CompletedProcess(command, 0, json.dumps([{"verificationResult": {"verified": True}}]), "")
        with patch.object(verifier.subprocess, "run", side_effect=run):
            report = verifier.verify(self.bundle, self.digest, self.commit)
        self.assertTrue(report["provenance_verified"])

    def test_bad_hash_never_invokes_gh(self):
        with patch.object(verifier.subprocess, "run") as run:
            with self.assertRaises(ValueError):
                verifier.verify(self.bundle, "0" * 64, self.commit)
            run.assert_not_called()

    def test_invalid_source_never_invokes_gh(self):
        for source in ("main", "abc123", "B" * 40):
            with self.subTest(source=source), patch.object(verifier.subprocess, "run") as run:
                with self.assertRaises(ValueError):
                    verifier.verify(self.bundle, self.digest, source)
                run.assert_not_called()

    def test_failed_signature_has_no_checksum_fallback(self):
        for code, output in ((1, ""), (0, "[]"), (0, "{}"), (0, '[{"verificationResult":null}]')):
            with self.subTest(output=output), patch.object(verifier.subprocess, "run", return_value=subprocess.CompletedProcess([], code, output, "")):
                with self.assertRaises(ValueError):
                    verifier.verify(self.bundle, self.digest, self.commit)

    def test_attestation_input_is_snapshotted(self):
        proof = self.base / "proof.json"
        proof.write_text("test signature input")
        def run(command, **kwargs):
            snapshot = Path(command[command.index("--bundle") + 1])
            self.assertNotEqual(snapshot, proof)
            self.assertEqual(snapshot.read_bytes(), proof.read_bytes())
            return subprocess.CompletedProcess(command, 0, '[{"verificationResult":{"verified":true}}]', "")
        with patch.object(verifier.subprocess, "run", side_effect=run):
            verifier.verify(self.bundle, self.digest, self.commit, proof)

    def test_publisher_attests_only_after_zip_verification(self):
        workflow = (ROOT / ".github/workflows/openjibo-runtime-preview-publish.yml").read_text()
        self.assertLess(workflow.index("Prepare and verify digest-pinned starter"), workflow.index("Attest verified starter ZIP provenance"))
        self.assertLess(workflow.index("Verify and retain starter ZIP attestation"), workflow.index("uses: actions/upload-artifact"))
        self.assertIn("subject-path: release-evidence/starter-${{ matrix.cpu_profile }}-preview.zip", workflow)
        self.assertIn('--source-commit "$GITHUB_SHA"', workflow)
        self.assertIn("environment: openjibo-runtime-release", workflow)


if __name__ == "__main__":
    unittest.main()
