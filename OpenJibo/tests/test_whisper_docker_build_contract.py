"""Offline checks for the distributable Whisper build settings."""

from pathlib import Path
import re
import unittest


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


if __name__ == "__main__":
    unittest.main()
