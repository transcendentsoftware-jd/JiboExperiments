"""Portable backup failures must not produce a completion manifest or leak secrets."""
import argparse
import copy
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/cloud/backup-self-hosted.py"
spec = importlib.util.spec_from_file_location("backup", SCRIPT)
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


def containers():
    shared = ["OPENJIBO_USER_ENCRYPT=private-encryption", "OPENJIBO_USER_SALT=private-salt",
              "OPENJIBO_POSTGRES_PASSWORD=private-password"]
    api = shared + ["OpenJibo__Deployment__Mode=self-hosted-isolated", "OpenJibo__State__Backend=PostgreSql",
                    "OpenJibo__PersonalMemory__Backend=PostgreSql", "OpenJibo__Media__Backend=File",
                    "OpenJibo__Media__DirectoryPath=/data/media", "OPENJIBO_POSTGRES_HOST=postgres",
                    "OPENJIBO_POSTGRES_PORT=5432", "OPENJIBO_POSTGRES_USER=openjibo"]
    result = {}
    for index, name in enumerate(("api", "migrate", "postgres"), 1):
        pg = name == "postgres"
        result[name] = {
            "Id": str(index) * 64, "Image": "sha256:" + str(index) * 64, "RestartCount": 0,
            "Config": {"Image": "test/image", "Env": (["POSTGRES_USER=openjibo", "POSTGRES_DB=openjibo_state"]
                                                      if pg else api if name == "api" else shared),
                       "Labels": {"com.docker.compose.project": "test", "com.docker.compose.service": name,
                                  "com.docker.compose.oneoff": "False"}},
            "Mounts": [{"Type": "volume", "Name": "pg-data" if pg else "api-data",
                        "Destination": "/var/lib/postgresql/data" if pg else "/data"}],
            "State": {"Running": pg, "Status": "running" if pg else "exited", "Paused": False,
                      "Restarting": False, "StartedAt": "start", "FinishedAt": "finish"},
        }
    return result


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.env = self.base / ".env"
        self.env.write_bytes(b"KEY=private-password\r\n")
        self.compose = self.base / "compose.yml"
        self.compose.write_text("services: {}\n")
        self.output = self.base / "backup"
        self.args = argparse.Namespace(directory=str(self.base), env_file=str(self.env),
                                       compose_file=[str(self.compose)], project_name="test",
                                       output=str(self.output), timeout=10, writers_stopped=True)

    def capture(self, after=None, failure=None):
        self.calls = []
        before = containers()

        def transfer(args, output=None, timeout=None):
            self.calls.append(args)
            if failure and failure in args:
                output.write(b"partial-secret")
                raise backup.BackupError("Transfer failed")
            output.write(b"PGDMP\x00\xff\r\n\x80binary" if "pg_dump" in args else b"tar\x00\xff\r\n")

        with mock.patch.object(backup, "observe", side_effect=[before, after or copy.deepcopy(before)]), \
                mock.patch.object(backup, "command", side_effect=transfer):
            return backup.capture(self.args)

    def test_binary_capture_keys_and_offline_verify(self):
        result = self.capture()
        self.assertTrue(result["backupCompleted"])
        self.assertEqual((self.output / "state.dump").read_bytes(), b"PGDMP\x00\xff\r\n\x80binary")
        self.assertEqual((self.output / "configuration.env").read_bytes(), self.env.read_bytes())
        private = json.loads((self.output / "runtime-private.json").read_bytes())
        self.assertIn("OPENJIBO_USER_ENCRYPT=private-encryption", private["containers"]["api"]["environment"])
        with mock.patch.object(backup, "command", side_effect=AssertionError("Offline verify invoked Docker")):
            report = backup.verify(self.output, result["manifestSha256"])
        self.assertTrue(report["backupBytesVerified"])
        self.assertFalse(report["restoreVerified"])
        self.assertNotIn("private-", json.dumps(result))
        self.assertEqual(len(self.calls), 3)
        self.assertTrue(all(c[:2] in (["docker", "exec"], ["docker", "cp"]) for c in self.calls))

    def test_dump_failure_leaves_incomplete_directory(self):
        with self.assertRaises(backup.BackupError):
            self.capture(failure="--dbname=openjibo_memory")
        self.assertTrue(self.output.is_dir())
        self.assertFalse((self.output / "manifest.json").exists())

    def test_restart_during_capture_refuses_completion(self):
        after = containers()
        after["api"]["State"]["StartedAt"] = "new-start"
        with self.assertRaises(backup.BackupError):
            self.capture(after=after)
        self.assertFalse((self.output / "manifest.json").exists())

    def test_postgres_health_probe_changes_do_not_invalidate_backup(self):
        after = containers()
        after["postgres"]["State"]["Health"] = {"Log": ["new probe"]}
        self.assertTrue(self.capture(after=after)["backupCompleted"])

    def test_existing_output_is_never_overwritten(self):
        self.output.mkdir()
        sentinel = self.output / "sentinel"
        sentinel.write_bytes(b"keep")
        with self.assertRaises(FileExistsError):
            self.capture()
        self.assertEqual(sentinel.read_bytes(), b"keep")

    def test_acknowledgement_required_before_docker(self):
        self.args.writers_stopped = False
        with mock.patch.object(backup, "command") as command, self.assertRaises(backup.BackupError):
            backup.capture(self.args)
        command.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_tampered_missing_extra_and_rewritten_manifest_refused(self):
        result = self.capture()
        state = self.output / "state.dump"
        original = state.read_bytes()
        state.write_bytes(b"X" * len(original))
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, result["manifestSha256"])
        state.write_bytes(original)
        extra = self.output / "unexpected"
        extra.touch()
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, result["manifestSha256"])
        extra.unlink()
        state.unlink()
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, result["manifestSha256"])
        state.write_bytes(original)
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, "a" * 64)

    def test_duplicate_manifest_keys_refused(self):
        self.capture()
        manifest = self.output / "manifest.json"
        original = manifest.read_text()
        manifest.write_text(original.replace('"schema":', '"schema": "duplicate", "schema":', 1))
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, backup.digest(manifest))

    def test_malformed_manifest_shapes_are_refused(self):
        self.capture()
        path = self.output / "manifest.json"
        valid = json.loads(path.read_bytes())
        invalid_record = copy.deepcopy(valid)
        invalid_record["files"]["state.dump"] = []
        for value in ([], {"schema": backup.SCHEMA, "files": []}, invalid_record):
            path.write_text(json.dumps(value))
            with self.subTest(value=value), self.assertRaises(backup.BackupError):
                backup.verify(self.output, backup.digest(path))

    def test_link_member_refused(self):
        result = self.capture()
        target = self.output / "state.dump"
        original = self.base / "original"
        target.rename(original)
        try:
            target.symlink_to(original)
        except OSError:
            self.skipTest("Host does not allow unprivileged symlink creation")
        with self.assertRaises(backup.BackupError):
            backup.verify(self.output, result["manifestSha256"])

    def test_running_or_paused_api_wrong_labels_and_custom_storage_refused(self):
        mutations = [
            lambda c: c["api"]["State"].update(Running=True, Status="running"),
            lambda c: c["api"]["State"].update(Paused=True),
            lambda c: c["api"]["Config"]["Labels"].update({"com.docker.compose.project": "other"}),
            lambda c: c["api"]["Config"]["Env"].append("OpenJibo__State__ConnectionString=private-other-db"),
            lambda c: c["api"]["Config"]["Env"].append("OPENJIBO_PERSONAL_MEMORY_SQL_CONNECTION_STRING=other"),
            lambda c: c["api"]["Mounts"].append({"Type": "volume", "Destination": "/data/media", "Name": "other"}),
            lambda c: c["api"]["Mounts"][0].update(Type="bind"),
            lambda c: c["migrate"]["Config"]["Env"].append("OPENJIBO_USER_SALT=other"),
        ]
        for mutate in mutations:
            candidate = containers()
            mutate(candidate)
            with self.subTest(mutate=mutate), self.assertRaises(backup.BackupError):
                backup.validate_containers(candidate, "test")

    def test_other_running_volume_writer_refused(self):
        fixture = containers()

        def fake(args, **unused):
            if args[:2] == ["docker", "inspect"]:
                return json.dumps([next(c for c in fixture.values() if c["Id"] == args[-1])]).encode()
            if args[:2] == ["docker", "ps"]:
                return b"other-container\n"
            return fixture[args[-1]]["Id"].encode()

        with mock.patch.object(backup, "command", side_effect=fake), self.assertRaises(backup.BackupError):
            backup.observe(["docker", "compose"], "test")

    def test_subprocess_binary_output_and_error_redaction(self):
        with tempfile.TemporaryFile() as stream:
            with mock.patch.object(backup.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
                backup.command(["docker", "exec"], output=stream)
                self.assertIs(run.call_args.kwargs["stdout"], stream)
                self.assertNotIn("text", run.call_args.kwargs)
        for fault in (OSError("private-password"), subprocess.TimeoutExpired("private-password", 1)):
            with mock.patch.object(backup.subprocess, "run", side_effect=fault), self.assertRaises(backup.BackupError) as error:
                backup.command(["docker"])
            self.assertNotIn("private-password", str(error.exception))


if __name__ == "__main__":
    unittest.main()
