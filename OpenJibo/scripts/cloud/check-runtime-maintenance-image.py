"""Owned local image check; no image pull, provider key, database or robot."""
import argparse,hashlib,json,re,subprocess,time,urllib.error,urllib.request,uuid
from pathlib import Path

def run(args):
    return subprocess.check_output(args,stderr=subprocess.PIPE,timeout=60).decode().strip()

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs):return None

def main():
    p=argparse.ArgumentParser();p.add_argument('--image',required=True);p.add_argument('--source-revision',required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    root=Path(__file__).resolve().parents[3]
    if not re.fullmatch('[0-9a-f]{40}',a.source_revision) or run(['git','-C',str(root),'rev-parse','HEAD'])!=a.source_revision:raise SystemExit('Exact checked-out source required.')
    if a.output.exists():raise SystemExit('New evidence output required.')
    image=json.loads(run(['docker','image','inspect',a.image]))[0]['Id']
    if not re.fullmatch('sha256:[0-9a-f]{64}',image):raise SystemExit('Existing immutable local image required.')
    name='openjibo-maintenance-'+uuid.uuid4().hex;container=None;stopped=False;removed=False;checks=[]
    def owned():
        d=json.loads(run(['docker','inspect',container]))[0]
        if d['Id']!=container or d['Image']!=image or d['Config']['Labels'].get('openjibo.test.scope')!=name:raise ValueError('Owned image container required.')
        return d
    try:
        container=run(['docker','run','--detach','--pull=never','--name',name,'--label','openjibo.test.scope='+name,
            '--read-only','--cap-drop=ALL','--security-opt=no-new-privileges','--tmpfs','/tmp:rw,noexec,nosuid,size=32m',
            '--publish','127.0.0.1::8080','--env','OpenJibo__Maintenance__Enabled=true',
            '--env','ASPNETCORE_URLS=http://+:8080','--env','DOTNET_EnableDiagnostics=0',image])
        d=owned();ports=d['NetworkSettings']['Ports']['8080/tcp']
        if len(ports)!=1 or ports[0]['HostIp']!='127.0.0.1' or not ports[0]['HostPort'].isdigit():raise ValueError('Loopback publication required.')
        origin='http://127.0.0.1:'+ports[0]['HostPort'];opener=urllib.request.build_opener(NoRedirect())
        def request(method,path,upgrade=False):
            headers={'Authorization':'Bearer synthetic-maintenance-probe'}
            if upgrade:headers.update({'Connection':'Upgrade','Upgrade':'websocket','Sec-WebSocket-Version':'13','Sec-WebSocket-Key':'AAAAAAAAAAAAAAAAAAAAAA=='})
            try:response=opener.open(urllib.request.Request(origin+path,method=method,headers=headers),timeout=2)
            except urllib.error.HTTPError as e:response=e
            with response:
                body=response.read(4097)
                if len(body)>4096 or response.headers.get('Cache-Control')!='no-store':raise ValueError('Bounded uncached response required.')
                return response.status,body
        for attempt in range(45):
            try:
                status,body=request('GET','/health/live')
                if status==200 and json.loads(body)=={'status':'alive','maintenance':True}:break
            except (OSError,ValueError):pass
            if not owned()['State']['Running']:raise ValueError('Closed container stopped before liveness.')
            time.sleep(0.5)
        else:raise ValueError('Closed liveness deadline exceeded.')
        checks.append('closed-liveness')
        for method,path,status,upgrade in [('HEAD','/health/live',200,False),('GET','/health/ready',503,False),('POST','/',404,False),('GET','/listen',404,True),('GET','/v1/proactive',404,True),('GET','/synthetic-robot-socket',404,True),('GET','/api/portal/status',404,False),('GET','/index.html',404,False),('POST','/health/live',404,False)]:
            actual,body=request(method,path,upgrade)
            if actual!=status or body:raise ValueError('Closed route differs.')
        checks.extend(['head-liveness','unready-health','closed-protocol','closed-listen','closed-proactive','closed-notification','closed-portal','closed-static','no-health-write'])
        d=owned()
        if not d['HostConfig']['ReadonlyRootfs'] or d['HostConfig']['CapDrop']!=['ALL'] or not any(s in ('no-new-privileges','no-new-privileges:true') for s in d['HostConfig']['SecurityOpt']) or any(m['Type']!='tmpfs' for m in d['Mounts']):raise ValueError('Read-only isolated image required.')
    finally:
        if container is None:
            partial=subprocess.run(['docker','inspect',name],capture_output=True,text=True,timeout=15)
            if partial.returncode==0:
                d=json.loads(partial.stdout)[0]
                if d['Image']!=image or d['Config']['Labels'].get('openjibo.test.scope')!=name:raise ValueError('Unknown partial container; cleanup refused.')
                container=d['Id']
        if container:
            owned();run(['docker','stop','--time','5',container]);stopped=not owned()['State']['Running']
            if not stopped:raise ValueError('Owned container stop failed.')
            run(['docker','rm',container]);removed=subprocess.run(['docker','inspect',container],capture_output=True,timeout=15).returncode!=0
    if not stopped or not removed:raise SystemExit('Owned cleanup incomplete.')
    files={'.github/workflows/openjibo-cloud-ci.yml','OpenJibo/Dockerfile','OpenJibo/.dockerignore','OpenJibo/scripts/docker-api-entrypoint.sh','OpenJibo/scripts/cloud/check-runtime-maintenance-image.py','OpenJibo/docs/runtime-maintenance.md'}
    import importlib.util
    spec=importlib.util.spec_from_file_location('maintenance_graph',root/'OpenJibo/tests/check_runtime_access.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);files.update(module.source_files(root))
    a.output.mkdir(parents=True,exist_ok=False);records=[]
    for name in sorted(files):
        data=(root/name).read_bytes();target='source__'+name.replace('/','__');(a.output/target).write_bytes(data);records.append(dict(source=name,file=target,sha256=hashlib.sha256(data).hexdigest()))
    proof=dict(schema='openjibo.runtime-maintenance-image.v1',sourceRevision=a.source_revision,imageId=image,containerId=container,status='verified',checks=checks,readOnlyFilesystem=True,capabilitiesDropped=True,persistentMounts=False,stopped=stopped,removed=removed,providerRequestsPerformed=False,realCredentialsRead=False,robotChanged=False,productionChanged=False,files=records)
    (a.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n');print('Verified closed maintenance image, hidden routes and owned container cleanup.')

if __name__=='__main__':main()
