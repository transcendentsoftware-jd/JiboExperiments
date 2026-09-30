"""Offline checks for the distributable Whisper build settings."""

from pathlib import Path
import re
import unittest
import importlib.util


DOCKERFILE = Path(__file__).resolve().parents[1] / "Dockerfile"


class WhisperDockerBuildContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dockerfile = DOCKERFILE.read_text(encoding="utf-8")
        cls.whisper_stage = dockerfile.split("FROM build AS whisper-true", 1)[1].split(
            "FROM debian:bookworm-slim AS whisper-false", 1
        )[0]

    def test_parallelism_has_bounded_configurable_default(self):
        stage = self.whisper_stage
        self.assertRegex(stage, r"(?m)^ARG WHISPER_BUILD_JOBS=2$")
        self.assertIn('case "${WHISPER_BUILD_JOBS}" in', stage)
        self.assertIn("*[!0-9]*", stage)
        self.assertRegex(stage, r'\[ "\$\{WHISPER_BUILD_JOBS\}" -ge 1 \]')
        self.assertRegex(stage, r'\[ "\$\{WHISPER_BUILD_JOBS\}" -le 32 \]')
        self.assertIn('-j"${WHISPER_BUILD_JOBS}"', stage)
        self.assertNotIn("$(nproc)", stage)

    def test_cpu_build_does_not_require_builder_or_modern_x86_features(self):
        configure = re.search(r"cmake -S /usr/bin/whisper\.cpp .*?&& cmake --build", self.whisper_stage, re.S)
        self.assertIsNotNone(configure)
        self.assertIn("ARG WHISPER_CPU_PROFILE=portable", self.whisper_stage)
        self.assertIn("portable) whisper_simd=OFF ;;", self.whisper_stage)
        self.assertIn("-DGGML_NATIVE=OFF", configure.group())
        for option in (
            "GGML_SSE42", "GGML_AVX", "GGML_AVX2",
            "GGML_FMA", "GGML_F16C", "GGML_BMI2",
        ):
            with self.subTest(option=option):
                self.assertIn(f'-D{option}="$whisper_simd"', configure.group())

    def test_optimized_profile_is_explicit_and_rejects_unknown_profiles(self):
        stage = self.whisper_stage
        self.assertIn('case "${WHISPER_CPU_PROFILE}" in', stage)
        self.assertIn('avx2) test "$(uname -m)" = x86_64', stage)
        self.assertIn('whisper_simd=ON ;;', stage)
        self.assertIn("*) echo 'WHISPER_CPU_PROFILE must be portable or avx2' >&2; exit 1 ;;", stage)
        self.assertNotIn("-DGGML_NATIVE=ON", stage)


class Avx2PreflightTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = DOCKERFILE.parent / "scripts/cloud/preflight-whisper-avx2.py"
        spec = importlib.util.spec_from_file_location("avx2_preflight", path)
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def test_all_processors_must_support_required_flags(self):
        flags = " ".join(sorted(self.module.REQUIRED))
        good = f"processor : 0\nflags : {flags}\n\nprocessor : 1\nflags : {flags}"
        self.assertTrue(self.module.assess("Linux", "x86_64", good)["avx2_cpu_prerequisites_passed"])
        bad = good.replace("processor : 1\nflags :", "processor : 1\nunknown :")
        self.assertFalse(self.module.assess("Linux", "x86_64", bad)["avx2_cpu_prerequisites_passed"])

    def test_missing_evidence_and_other_platforms_fail_closed(self):
        flags = " ".join(sorted(self.module.REQUIRED))
        for system, machine, info in [("Linux", "x86_64", ""), ("Windows", "x86_64", f"processor:0\nflags:{flags}"), ("Linux", "aarch64", f"processor:0\nflags:{flags}")]:
            self.assertFalse(self.module.assess(system, machine, info)["avx2_cpu_prerequisites_passed"])

    def test_each_required_flag_is_checked(self):
        for absent in self.module.REQUIRED:
            flags = " ".join(sorted(self.module.REQUIRED - {absent}))
            result = self.module.assess("Linux", "x86_64", f"processor:0\nflags:{flags}")
            self.assertFalse(result["avx2_cpu_prerequisites_passed"])
            self.assertEqual([absent], result["missing_flags"])


if __name__ == "__main__":
    unittest.main()
