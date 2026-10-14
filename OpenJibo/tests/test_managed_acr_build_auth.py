"""Command-level coverage for the managed ACR publish wrappers."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO_ROOT = Path(__file__).resolve().parents[1]
BASH_WRAPPER = REPO_ROOT / "scripts" / "cloud" / "publish-openjibo-managed.sh"
POWERSHELL_WRAPPER = REPO_ROOT / "scripts" / "cloud" / "Publish-OpenJiboManaged.ps1"


class ManagedAcrBuildAuthTests(unittest.TestCase):
    def run_bash_wrapper(self, mode, show_status=0, build_status=0):
        with tempfile.TemporaryDirectory() as temp_dir:
            temp = Path(temp_dir)
            az = temp / "az"
            az.write_text(
                "#!/usr/bin/env bash\n"
                "if [[ $2 == show ]]; then\n"
                "  printf '%s\\n' \"$@\" > \"$MOCK_SHOW_ARGS\"\n"
                "  printf '%s\\n' \"$MOCK_MODE\"\n"
                "  exit \"$MOCK_SHOW_STATUS\"\n"
                "fi\n"
                "printf '%s\\n' \"$@\" > \"$MOCK_BUILD_ARGS\"\n"
                "exit \"$MOCK_BUILD_STATUS\"\n",
                encoding="utf-8",
            )
            az.chmod(0o755)
            show_capture = temp / "show-args"
            build_capture = temp / "build-args"
            env = dict(os.environ)
            env.update(
                PATH=f"{temp}{os.pathsep}{env.get('PATH', '')}",
                MOCK_MODE=mode,
                MOCK_SHOW_STATUS=str(show_status),
                MOCK_BUILD_STATUS=str(build_status),
                MOCK_SHOW_ARGS=str(show_capture),
                MOCK_BUILD_ARGS=str(build_capture),
            )
            result = subprocess.run(
                ["bash", str(BASH_WRAPPER), "--registry-name", "testregistry"],
                capture_output=True,
                text=True,
                env=env,
                check=False,
            )
            show_args = show_capture.read_text(encoding="utf-8").splitlines() if show_capture.exists() else None
            build_args = build_capture.read_text(encoding="utf-8").splitlines() if build_capture.exists() else None
            return result, show_args, build_args

    def run_powershell_wrapper(self, mode, show_status=0, build_status=0):
        if not shutil.which("pwsh"):
            self.skipTest("PowerShell is required")

        with tempfile.TemporaryDirectory() as temp_dir:
            temp = Path(temp_dir)
            show_capture = temp / "show-args.json"
            build_capture = temp / "build-args.json"
            env = dict(os.environ)
            env.update(
                MOCK_MODE=mode,
                MOCK_SHOW_STATUS=str(show_status),
                MOCK_BUILD_STATUS=str(build_status),
                MOCK_SHOW_ARGS=str(show_capture),
                MOCK_BUILD_ARGS=str(build_capture),
            )
            wrapper_path = str(POWERSHELL_WRAPPER).replace("'", "''")
            command = (
                "function az { "
                "if ($args[1] -eq 'show') { "
                "[IO.File]::WriteAllText($env:MOCK_SHOW_ARGS, (ConvertTo-Json -InputObject @($args) -Compress)); "
                "$global:LASTEXITCODE = [int]$env:MOCK_SHOW_STATUS; "
                "return $env:MOCK_MODE "
                "}; "
                "[IO.File]::WriteAllText($env:MOCK_BUILD_ARGS, (ConvertTo-Json -InputObject @($args) -Compress)); "
                "$global:LASTEXITCODE = [int]$env:MOCK_BUILD_STATUS "
                "}; "
                f"& '{wrapper_path}' -RegistryName testregistry; exit $LASTEXITCODE"
            )
            result = subprocess.run(
                ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
                capture_output=True,
                text=True,
                env=env,
                check=False,
            )
            show_args = json.loads(show_capture.read_text(encoding="utf-8")) if show_capture.exists() else None
            build_args = json.loads(build_capture.read_text(encoding="utf-8")) if build_capture.exists() else None
            return result, show_args, build_args

    def assert_wrapper_modes(self, run_wrapper):
        expected_show = [
            "acr", "show", "--name", "testregistry", "--query", "roleAssignmentMode",
            "--output", "tsv", "--only-show-errors",
        ]
        for mode, expected_auth in (
            ("LegacyRegistryPermissions", []),
            ("AbacRepositoryPermissions", ["--source-acr-auth-id", "[caller]"]),
        ):
            with self.subTest(mode=mode):
                result, show_args, build_args = run_wrapper(mode)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(show_args, expected_show)
                self.assertIsNotNone(build_args)
                self.assertEqual(build_args[:2], ["acr", "build"])
                self.assertEqual(
                    build_args.count("--source-acr-auth-id"),
                    1 if expected_auth else 0,
                )
                if expected_auth:
                    index = build_args.index("--source-acr-auth-id")
                    self.assertEqual(build_args[index:index + 2], expected_auth)
                self.assertEqual(
                    build_args[build_args.index("--build-arg") + 1],
                    "ENABLE_LOCAL_WHISPER=false",
                )

        for mode, show_status in (("", 0), ("UnexpectedMode", 0), ("AbacRepositoryPermissions", 7)):
            with self.subTest(mode=mode, show_status=show_status):
                result, show_args, build_args = run_wrapper(mode, show_status=show_status)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(show_args, expected_show)
                self.assertIsNone(build_args)

        result, _, build_args = run_wrapper("AbacRepositoryPermissions", build_status=23)
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertIsNotNone(build_args)

    def test_bash_wrapper(self):
        if os.name == "nt" or not shutil.which("bash"):
            self.skipTest("A POSIX Bash environment is required")
        self.assert_wrapper_modes(self.run_bash_wrapper)

    def test_powershell_wrapper(self):
        self.assert_wrapper_modes(self.run_powershell_wrapper)


if __name__ == "__main__":
    unittest.main()
