#!/usr/bin/env python3
"""Read-only host prerequisite check for the existing Compose starter."""
import argparse
import json
import platform
import re
import shutil
import subprocess


def probe(command, timeout):
    try:
        result = subprocess.run(command, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, timeout=timeout,
                                check=False, shell=False)
    except subprocess.TimeoutExpired:
        return 'timeout', None
    except OSError:
        return 'unavailable', None
    if result.returncode != 0:
        return 'failed', None
    if len(result.stdout) > 8192:
        return 'invalid-response', None
    try:
        return 'ok', result.stdout.decode('utf-8').strip()
    except UnicodeError:
        return 'invalid-response', None


def inspect(timeout=5, system=None, machine=None, find=shutil.which, run=probe):
    system = system or platform.system()
    machine = machine or platform.machine()
    # Do not include paths, environment, CLI errors, config files or credentials.
    family = {'Windows': 'windows', 'Darwin': 'macos', 'Linux': 'linux'}.get(system, 'other')
    host_arch = {'x86_64': 'amd64', 'AMD64': 'amd64', 'aarch64': 'arm64',
                 'arm64': 'arm64'}.get(machine, 'other')
    checks = [{'name': 'host-os', 'status': 'ok' if family != 'other' else 'unsupported'}]
    docker = find('docker')
    checks.append({'name': 'docker-cli', 'status': 'ok' if docker else 'missing'})
    engine_os = engine_arch = None
    if docker:
        status, output = run([docker, 'compose', 'version', '--short'], timeout)
        if status == 'ok':
            version = re.fullmatch(r'v?(\d+)\.(\d+)\.[0-9A-Za-z.+_-]+', output or '')
            status = 'ok' if version and int(version[1]) >= 2 else 'unsupported-version'
        checks.append({'name': 'compose-v2', 'status': status})
        status, output = run([docker, 'version', '--format', '{{json .Server}}'], timeout)
        if status == 'ok':
            try:
                server = json.loads(output)
                engine_os, engine_arch = server['Os'], server['Arch']
                if engine_os not in ('linux', 'windows') or engine_arch not in ('amd64', 'arm64'):
                    raise ValueError()
            except (ValueError, KeyError, TypeError):
                status, engine_os, engine_arch = 'invalid-response', None, None
        checks.append({'name': 'docker-engine', 'status': status})
        if status == 'ok':
            checks.append({'name': 'linux-containers', 'status': 'ok' if engine_os == 'linux' else 'switch-required'})
            # Current preview evidence is Linux/amd64. Do not silently select emulation.
            checks.append({'name': 'preview-architecture', 'status': 'ok' if engine_arch == 'amd64' else 'unvalidated'})
    ready = all(check['status'] == 'ok' for check in checks)
    actions = []
    for check in checks:
        if check['status'] == 'ok':
            continue
        actions.append({
            'host-os': 'Use the documented Windows, macOS or Linux host path.',
            'docker-cli': 'Install Docker with the Compose v2 plugin, then rerun this check.',
            'compose-v2': 'Make docker compose version --short work with Compose v2 or later.',
            'docker-engine': 'Start the selected Docker engine and check its context/permissions; rerun this check.',
            'linux-containers': 'Select Linux containers in the Docker engine.',
            'preview-architecture': 'Use a validated Linux/amd64 engine; ARM64 and emulated execution need separate validation.',
        }[check['name']])
    return {'schema': 'openjibo.host-prerequisites.v1', 'hostOs': family,
            'hostArchitecture': host_arch, 'engineOs': engine_os, 'engineArchitecture': engine_arch,
            'checks': checks, 'prerequisitesPassed': ready, 'nextActions': actions,
            'installCertified': False, 'robotConversionVerified': False,
            'filesModified': False, 'containersStarted': False,
            'documentation': 'docs/platform-setup-guide.md'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', action='store_true', help='Print a redacted machine-readable report.')
    parser.add_argument('--timeout', type=int, default=5, choices=range(1, 31), metavar='1-30',
                        help='Maximum seconds for each of the two read-only Docker queries (default: 5).')
    args = parser.parse_args()
    report = inspect(args.timeout)
    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print('Host prerequisites: ' + ('passed' if report['prerequisitesPassed'] else 'needs attention'))
        for check in report['checks']:
            print('  ' + check['name'] + ': ' + check['status'])
        for action in report['nextActions']:
            print('  Next: ' + action)
        print('Continue with docs/platform-setup-guide.md. This check does not certify an installation or robot conversion.')
    return 0 if report['prerequisitesPassed'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
