"""Retain exact compilation sources and mandatory runtime-access acceptance cases."""
import argparse,hashlib,json,os,re,subprocess,xml.etree.ElementTree as ET
from pathlib import Path

def source_files(root):
    files={'.github/workflows/openjibo-cloud-ci.yml','OpenJibo/docs/runtime-access-authorization.md','OpenJibo/tests/check_runtime_access.py'}
    seen=set()
    def visit(project):
        project=project.resolve()
        if not project.is_relative_to(root) or project.is_symlink():raise ValueError('In-repository project required.')
        if project in seen:return
        seen.add(project);files.add(project.relative_to(root).as_posix())
        for suffix in ('*.cs','*.csproj'):
            files.update(p.relative_to(root).as_posix() for p in project.parent.rglob(suffix) if not {'bin','obj'}.intersection(p.relative_to(project.parent).parts))
        for parent in (project.parent,*project.parents):
            if not parent.is_relative_to(root):continue
            for name in ('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.Config','packages.lock.json'):
                if (parent/name).is_file():files.add((parent/name).relative_to(root).as_posix())
        for item in ET.parse(project).getroot().iter('ProjectReference'):visit(project.parent/item.attrib['Include'].replace('\\','/'))
    visit(root/'OpenJibo/tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj')
    if len(seen)<5 or len(files)<200:raise ValueError('Full compilation graph required.')
    return sorted(files)

def main():
    p=argparse.ArgumentParser();p.add_argument('--source-revision',required=True);p.add_argument('--trx',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    root=Path(__file__).resolve().parents[2]
    if not re.fullmatch('[0-9a-f]{40}',a.source_revision):raise SystemExit('Exact source required.')
    if subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD']).decode().strip()!=a.source_revision:raise SystemExit('Checked-out source differs.')
    raw=a.trx.read_bytes();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};test=ET.fromstring(raw);units=test.findall('t:Results/t:UnitTestResult',ns)
    required={'.RuntimeAccessTests.':19,'.RuntimeProtocolAccessTests.':7,
      '.Paid_profile_requires_current_full_evidence_and_explicit_identity_before_accept':7,
      '.Previously_paid_token_cannot_reconnect_after_access_is_suspended':3,
      '.Every_idle_robot_channel_closes_on_lost_authority_and_requires_fresh_reconnect':9,
      '.Credential_bound_notification_socket_uses_its_own_fresh_access_decision':1,
      '.Every_send_overload_checks_the_connection_guard':2,
      '.Expiring_paid_grant_cancels_an_already_open_idle_socket':1,
      '.RuntimeAccessNotificationEvidenceRechecksRevocationAndSecretAcrossReplicas':1}
    for marker,count in required.items():
        cases=[x for x in units if marker in x.get('testName','')]
        if len(cases)!=count or any(x.get('outcome')!='Passed' for x in cases):raise SystemExit('All required runtime-access cases must pass without skips: '+marker)
    counters=test.find('t:ResultSummary/t:Counters',ns)
    if counters is None or int(counters.get('failed','0'))!=0:raise SystemExit('Full run contains failures.')
    output=a.output.resolve()
    if os.name=='nt':output=Path('\\\\?\\'+str(output))
    output.mkdir(parents=True,exist_ok=False);records=[]
    for name in source_files(root):
        content=(root/name).read_bytes();target='source__'+name.replace('/','__');(output/target).write_bytes(content);records.append(dict(source=name,file=target,sha256=hashlib.sha256(content).hexdigest()))
    (output/'runtime-access.trx').write_bytes(raw)
    proof=dict(schema='openjibo.runtime-access-candidate.v1',sourceRevision=a.source_revision,testsPassed=sum(required.values()),testsSkipped=0,
       fullRunPassed=int(counters.get('passed','0')),fullRunSkipped=int(counters.get('notExecuted','0')),testResultsSha256=hashlib.sha256(raw).hexdigest(),
       realCredentialsRead=False,providerRequestsPerformed=False,nativePublicationPerformed=False,robotChanged=False,productionChanged=False,files=records)
    (output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n');print('Verified '+str(sum(required.values()))+' runtime access cases and '+str(len(records))+' exact compilation source files.')

if __name__=='__main__':main()
