import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('host_check', Path(__file__).parents[1] / 'scripts/cloud/inspect-openjibo-host.py')
HOST = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HOST)


class HostChecks(unittest.TestCase):
    def test_native_runner_os_with_no_docker(self):
        r = HOST.inspect(find=lambda _: None)
        self.assertIn(r['hostOs'], ('windows', 'macos', 'linux'))
        self.assertFalse(r['prerequisitesPassed'])
        self.assertFalse(r['filesModified'])
        self.assertFalse(r['containersStarted'])

    def report(self, system='Linux', machine='x86_64', engine='linux', arch='amd64', compose='2.30.1'):
        def run(args, timeout):
            self.assertEqual(timeout, 5)
            return ('ok', compose if args[1] == 'compose' else '{"Os":"' + engine + '","Arch":"' + arch + '"}')
        return HOST.inspect(system=system, machine=machine, find=lambda _: 'docker', run=run)

    def test_three_host_os_paths_use_engine_architecture(self):
        for system, machine in [('Linux', 'x86_64'), ('Windows', 'AMD64'), ('Darwin', 'arm64')]:
            with self.subTest(system=system):
                r = self.report(system, machine)
                self.assertTrue(r['prerequisitesPassed'])
                self.assertFalse(r['installCertified'])
                self.assertFalse(r['robotConversionVerified'])

    def test_missing_docker_never_runs_commands(self):
        r = HOST.inspect(system='Windows', machine='AMD64', find=lambda _: None,
                         run=lambda *_: self.fail('command must not run'))
        self.assertFalse(r['prerequisitesPassed'])
        self.assertEqual(r['checks'][-1]['status'], 'missing')

    def test_windows_engine_arm_and_old_compose_block(self):
        for kwargs in ({'engine': 'windows'}, {'arch': 'arm64'}, {'compose': '1.29.2'}, {'compose': 'garbage'}):
            with self.subTest(kwargs=kwargs):
                self.assertFalse(self.report(**kwargs)['prerequisitesPassed'])

    def test_failed_hung_and_malformed_engine_block_without_echoing_output(self):
        for status, output in [('timeout', None), ('failed', 'secret-error'), ('ok', '{"token":"secret"}'), ('ok', '[]')]:
            r = HOST.inspect(system='Linux', machine='x86_64', find=lambda _: 'docker', run=lambda *_: (status, output))
            self.assertFalse(r['prerequisitesPassed'])
            self.assertNotIn('secret', str(r))

    def test_subprocess_is_read_only_bounded_and_never_shell(self):
        with patch.object(HOST.subprocess, 'run', side_effect=subprocess.TimeoutExpired('docker', 3)) as run:
            self.assertEqual(HOST.probe(['docker', 'version'], 3), ('timeout', None))
            self.assertEqual(run.call_args.kwargs['timeout'], 3)
            self.assertFalse(run.call_args.kwargs['shell'])
            self.assertEqual(run.call_args.kwargs['stderr'], subprocess.DEVNULL)


if __name__ == '__main__':
    unittest.main()
