"""Retain a synthetic setup packet, or check the actual disposable CI runtime wire contract."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import re
import shutil
import sys

sys.path.insert(0,str(Path(__file__).resolve().parent))
import test_self_hosted_readiness as fixture
Q=fixture.Q


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--source-revision',required=True)
    parser.add_argument('--live-origin')
    args=parser.parse_args()
    assert re.fullmatch('[0-9a-f]{40}',args.source_revision)
    root=args.output.resolve(); root.mkdir(exist_ok=False)
    kwargs=(dict(probe_origin=args.live_origin) if args.live_origin else
            dict(host=lambda **_:fixture.host(platform.system(),platform.machine()),
                 request=lambda *_:dict(status='ok',profile=fixture.profile())))
    report=Q.build('http://192.168.50.20:8080','http://hub.local:8080',root/'packet',True,**kwargs)
    assert report['runtime']['status']=='ok' and report['runtime']['profile']['persistentStorageConfigured']
    assert report['targetExported'] is (args.live_origin is None)
    assert not any(report[field] for field in ('readyForApply','databaseConnectivityVerified','restartPersistenceVerified',
                                               'restoreVerified','robotProtocolVerified','oobeDeploymentVerified','repeatDeploymentVerified'))
    for item in report['files']:
        assert Q.sha((root/'packet'/item['file']).read_bytes())==item['sha256']
    sources=root/'reviewed-source'; sources.mkdir()
    for name in ('prepare-self-hosted-readiness.py','inspect-openjibo-host.py'):
        shutil.copyfile(Path(Q.__file__).with_name(name),sources/name)
    cloud=Path(__file__).parents[1]
    for name in ('Program.cs','Hosting/SelfHostedConfigurationProfile.cs'):
        source=cloud/'src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api'/name
        shutil.copyfile(source,sources/Path(name).name)
    shutil.copyfile(Path(__file__),sources/'check_self_hosted_readiness.py')
    proof=dict(schema='openjibo.self-hosted-readiness-check.v1',sourceRevision=args.source_revision,
               liveDisposableRuntimeObserved=bool(args.live_origin),syntheticHostAndRuntime=not bool(args.live_origin),
               targetContractChecked=report['targetExported'],apiOriginMatchesProbe=report['apiOriginMatchesProbe'],
               readyForApply=False,robotAcceptanceVerified=False,
               files=[dict(file=p.relative_to(root).as_posix(),sizeBytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest())
                      for p in sorted(root.rglob('*')) if p.is_file()])
    (root/'proof.json').write_text(json.dumps(proof,indent=2)+'\n',encoding='utf-8')
    print('Self-hosted readiness packet passed: configuration wire contract, target export boundary and false robot/readiness acceptance flags.')


if __name__=='__main__': main()
