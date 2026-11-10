"""Exercise the shipped image with fresh owned PostgreSQL and synthetic keys."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

sys.path.insert(0,str(Path(__file__).resolve().parent))
from registry_acceptance import Acceptance, Fixture, POSTGRES_IMAGE, Refusal


def command(args, timeout=60):
    result=subprocess.run(args,capture_output=True,timeout=timeout)
    if result.returncode: raise Refusal('owned-command-failed')
    return result.stdout.decode().strip()


class DockerFixture:
    def __init__(self, image, fixture, private):
        self.image, self.fixture, self.private=image, fixture, private
        self.prefix='registry-'+fixture.scope[:12]
        self.network=self.prefix+'-net';self.pg=self.prefix+'-pg';self.api=self.prefix+'-api';self.migrate=self.prefix+'-migrate'
        self.created=[];self.network_created=False;self.pg_started=None;self.retired=[]

    def owned(self,name,image=None):
        value=json.loads(command(['docker','inspect',name]))[0]
        if value['Config']['Labels'].get('openjibo.test.scope')!=self.fixture.scope or image and value['Image']!=image:
            raise Refusal('unowned-container-refused')
        return value

    def create(self,name,image,extra):
        self.created.append((name,image)) # Reserve only our UUID-derived name; recover ambiguous create through labels.
        return command(['docker','create','--pull=never','--name',name,'--label','openjibo.test.scope='+self.fixture.scope,
            '--network',self.network,*extra,image])

    def env_file(self,name,values):
        path=self.private/name
        with open(os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'w') as stream:
            stream.write(''.join(k+'='+v+'\n' for k,v in values.items()))
        return str(path)

    def start_database(self):
        self.network_created=True
        command(['docker','network','create','--label','openjibo.test.scope='+self.fixture.scope,self.network])
        pg=command(['docker','image','inspect',POSTGRES_IMAGE,'--format','{{.Id}}'])
        env=self.env_file('pg.env',{'POSTGRES_USER':'fixture_admin','POSTGRES_PASSWORD':self.fixture.admin_password,'POSTGRES_DB':'registry_state'})
        self.create(self.pg,pg,['--env-file',env,'--tmpfs','/var/lib/postgresql/data:rw,nosuid,size=512m'])
        sql=self.private/'init.sql';sql.write_text(self.fixture.init_sql())
        command(['docker','cp',str(sql),self.pg+':/docker-entrypoint-initdb.d/01-fixture.sql'])
        command(['docker','start',self.pg])
        for _ in range(60):
            result=subprocess.run(['docker','exec',self.pg,'pg_isready','-h','127.0.0.1','-U','fixture_admin','-d','registry_state'],capture_output=True,timeout=5)
            if result.returncode==0:break
            time.sleep(.5)
        else:raise Refusal('database-readiness')
        self.pg_started=self.owned(self.pg)['State']['StartedAt']
        values={'OpenJibo__State__ConnectionString':self.fixture.connection(self.pg,'registry_state',True),
                'OpenJibo__PersonalMemory__ConnectionString':self.fixture.connection(self.pg,'registry_memory',True)}
        migrate_env=self.env_file('migrate.env',values)
        self.created.append((self.migrate,self.image))
        command(['docker','create','--pull=never','--name',self.migrate,'--label','openjibo.test.scope='+self.fixture.scope,
            '--network',self.network,'--env-file',migrate_env,self.image,'/app/migrations/Jibo.Cloud.Migrations.dll','--apply','--target','all'])
        command(['docker','start','--attach',self.migrate],timeout=90)
        if self.owned(self.migrate)['State']['ExitCode']!=0:raise Refusal('migration-refused')

    def start_api(self,phase):
        if self.pg_started!=self.owned(self.pg)['State']['StartedAt']:raise Refusal('database-restarted')
        if any(name==self.api for name,_ in self.created):
            old=self.owned(self.api,self.image)['Id'];command(['docker','stop','--time','10',self.api])
            if self.owned(self.api,self.image)['State']['Running']:raise Refusal('old-api-not-stopped')
            command(['docker','rm',self.api]);self.created=[r for r in self.created if r[0]!=self.api];self.retired.append(old)
        env=self.env_file(phase+'.env',self.fixture.environment(phase,self.pg))
        self.create(self.api,self.image,['--cap-drop=ALL','--security-opt=no-new-privileges',
            '--env-file',env,'--publish','127.0.0.1::8080','--tmpfs','/tmp:rw,noexec,nosuid,size=64m'])
        command(['docker','start',self.api]);state=self.owned(self.api,self.image)
        ports=state['NetworkSettings']['Ports']['8080/tcp']
        if len(ports)!=1 or ports[0]['HostIp']!='127.0.0.1':raise Refusal('api-publication-refused')
        return 'http://127.0.0.1:'+ports[0]['HostPort']

    def cleanup(self):
        for name,image in reversed(self.created):
            result=subprocess.run(['docker','inspect',name],capture_output=True,timeout=15)
            if result.returncode:continue
            self.owned(name,image);command(['docker','rm','--force',name])
        if self.network_created:
            found=subprocess.run(['docker','network','inspect',self.network],capture_output=True,timeout=15)
            if found.returncode:return
            network=json.loads(found.stdout)[0]
            if network['Labels'].get('openjibo.test.scope')!=self.fixture.scope or network['Containers']:
                raise Refusal('network-cleanup-refused')
            command(['docker','network','rm',self.network])


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--image',required=True);p.add_argument('--source-revision',required=True);p.add_argument('--output',required=True,type=Path);a=p.parse_args()
    root=Path(__file__).resolve().parents[3]
    if not re.fullmatch('[0-9a-f]{40}',a.source_revision) or command(['git','-C',str(root),'rev-parse','HEAD'])!=a.source_revision:
        raise Refusal('checked-source-required')
    if a.output.exists():raise Refusal('new-output-required')
    image=command(['docker','image','inspect',a.image,'--format','{{.Id}}'])
    if not re.fullmatch('sha256:[0-9a-f]{64}',image):raise Refusal('immutable-local-image-required')
    fixture=Fixture();proof={'schema':'openjibo.trusted-credential-image-acceptance.v1','sourceRevision':a.source_revision,'imageId':image,
        'realRobotOrProviderCredentialsSupplied':False,'robotContact':False,'hostedChanges':False,'status':'failed'}
    with tempfile.TemporaryDirectory(prefix='registry-image-private-') as private:
        controller=DockerFixture(image,fixture,Path(private));client=None
        try:
            controller.start_database()
            for phase in ('baseline','removed','rotated'):
                base=controller.start_api(phase)
                if client is None:client=Acceptance(fixture,base)
                else:client.set_origin(base)
                client.ready();getattr(client,phase)()
            if client.posts!=12 or len(client.results)!=33 or len(controller.retired)!=2:raise Refusal('acceptance-counts-refused')
            proof.update(status='verified',cases=client.results,tokenPosts=client.posts,oldApiProcessesStopped=2,
                databaseProcessPreserved=True,unchangedBearerPreserved=True)
        finally:
            proof['ownedResourcesRemoved']=False
            try:
                controller.cleanup();proof['ownedResourcesRemoved']=True
            finally:
                a.output.mkdir(parents=True,exist_ok=False)
                if client is not None:proof['cases']=client.results
                (a.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n')
    spec=importlib.util.spec_from_file_location('registry_graph',root/'OpenJibo/tests/check_runtime_access.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    files=set(module.source_files(root))|{'OpenJibo/scripts/cloud/registry_acceptance.py','OpenJibo/scripts/cloud/check-registry-acceptance-image.py',
        'OpenJibo/tests/test_registry_acceptance.py','OpenJibo/docs/registry-image-acceptance.md','OpenJibo/Dockerfile','OpenJibo/scripts/docker-api-entrypoint.sh'}
    records=[]
    for name in sorted(files):
        data=(root/name).read_bytes();target='source__'+name.replace('/','__');(a.output/target).write_bytes(data);records.append({'source':name,'file':target,'sha256':hashlib.sha256(data).hexdigest()})
    proof['files']=records;(a.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n')
    print('Verified 33 synthetic image cases, 12 token POSTs, stopped-process rotation and owned cleanup.')


if __name__=='__main__':main()
