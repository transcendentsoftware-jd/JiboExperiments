#!/usr/bin/env python3
"""Observe host/runtime configuration and export a self-hosted conversion target."""
import argparse
from datetime import datetime, timezone
import hashlib
import html
import importlib.util
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import ssl
import stat
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

spec=importlib.util.spec_from_file_location('host_check',Path(__file__).with_name('inspect-openjibo-host.py'))
H=importlib.util.module_from_spec(spec); spec.loader.exec_module(H)
LIMIT=8192
LAN4=tuple(ipaddress.ip_network(value) for value in ('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','169.254.0.0/16'))
LAN6=ipaddress.ip_network('fc00::/7')


def require(value):
    if not value: raise ValueError('self_hosted_readiness_refused')


def canonical(value): return json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=True).encode('ascii')
def sha(raw): return hashlib.sha256(raw).hexdigest()


def origin(value,allow_http,loopback=False):
    require(type(value) is str and 0<len(value)<=512 and value.isascii() and all(33<=ord(c)<=126 for c in value))
    parsed=urllib.parse.urlsplit(value)
    require(parsed.scheme in ('http','https') and parsed.path in ('','/') and not parsed.query and not parsed.fragment)
    require(parsed.username is None and parsed.password is None and parsed.hostname)
    hostname=parsed.hostname.lower(); port=parsed.port
    require('%' not in hostname)
    require(port is None or 1<=port<=65535)
    try:
        address=ipaddress.ip_address(hostname)
    except ValueError:
        require(not re.fullmatch(r'[0-9.]+',hostname) and ':' not in hostname)
        require(hostname!='localhost' or loopback)
        require(hostname=='localhost' or ('.' in hostname and len(hostname)<=253 and all(re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?',part) for part in hostname.split('.'))))
        local=hostname=='localhost' or hostname.endswith('.local')
    else:
        require(not address.is_loopback or loopback)
        local=address.is_loopback or (any(address in net for net in LAN4) if address.version==4 else address in LAN6)
        hostname='['+address.compressed+']' if address.version==6 else address.compressed
    require(parsed.scheme!='http' or (allow_http and local))
    return parsed.scheme+'://'+hostname+(':'+str(port) if port and port!=(443 if parsed.scheme=='https' else 80) else '')


def parse(raw):
    require(type(raw) is bytes and 0<len(raw)<=LIMIT)
    def pairs(items):
        value={}
        for key,item in items: require(key not in value); value[key]=item
        return value
    value=json.loads(raw.decode('utf-8'),object_pairs_hook=pairs,parse_constant=lambda _:require(False))
    require(type(value) is dict and len(value)<=32)
    return value


def profile(value):
    expected={'schema','service','deploymentMode','stateBackend','personalMemoryBackend','persistentStorageConfigured',
              'tokenlessSingleRobotHubConfigured','configurationOnly','databaseConnectivityVerified','robotCompatibilityVerified','readyForApply'}
    require(type(value) is dict and set(value)==expected)
    require(value['schema']=='openjibo.self-hosted-configuration.v1' and value['service']=='OpenJibo Cloud Api')
    require(value['deploymentMode']=='self-hosted-isolated')
    require(value['stateBackend'] in ('postgresql','sqlite','other-or-unspecified') and value['personalMemoryBackend'] in ('postgresql','sqlite','other-or-unspecified'))
    for key in expected-{'schema','service','deploymentMode','stateBackend','personalMemoryBackend'}: require(type(value[key]) is bool)
    require(value['configurationOnly'] and not any(value[k] for k in ('databaseConnectivityVerified','robotCompatibilityVerified','readyForApply')))
    require(value['persistentStorageConfigured'] is (value['stateBackend']=='postgresql' and value['personalMemoryBackend']=='postgresql'))
    return value


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs): return None


def fetch(origin_value,timeout):
    """Runs only in the supervised child: no redirects, proxy, credentials or raw output."""
    try:
        normalized=origin(origin_value,True,loopback=True)
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect())
        request=urllib.request.Request(normalized+'/health/self-hosted',headers={'Accept':'application/json'},method='GET')
        with opener.open(request,timeout=timeout) as response:
            require(response.status==200 and response.headers.get_content_type()=='application/json')
            result=profile(parse(response.read(LIMIT+1)))
        return dict(status='ok',profile=result)
    except urllib.error.HTTPError: return dict(status='http-error',profile=None)
    except urllib.error.URLError as error:
        return dict(status='tls-error' if isinstance(error.reason,ssl.SSLError) else 'unavailable',profile=None)
    except (socket.timeout,TimeoutError): return dict(status='timeout',profile=None)
    except (ValueError,OSError,UnicodeError,TypeError,KeyError,RecursionError): return dict(status='invalid-response',profile=None)


def query(origin_value,timeout):
    try:
        result=subprocess.run([sys.executable,'-I','-B',str(Path(__file__).resolve()),'--_probe',origin_value,str(timeout)],
                              stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,timeout=timeout,check=False,shell=False)
        require(result.returncode==0)
        value=parse(result.stdout)
        require(set(value)=={'status','profile'} and value['status'] in ('ok','http-error','tls-error','unavailable','timeout','invalid-response'))
        if value['status']=='ok': profile(value['profile'])
        else: require(value['profile'] is None)
        return value
    except subprocess.TimeoutExpired: return dict(status='timeout',profile=None)
    except (ValueError,OSError,UnicodeError,RecursionError): return dict(status='invalid-response',profile=None)


def ordinary(path):
    require(not str(path).startswith(('\\\\','//')))
    if os.name=='nt': require(':' not in str(Path(path).absolute())[2:])
    path=Path(path).absolute()
    for item in (path,*path.parents):
        info=item.lstat(); require(not stat.S_ISLNK(info.st_mode) and not getattr(info,'st_file_attributes',0)&1024)
    return path


def write(path,raw):
    with os.fdopen(os.open(str(path),os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,'O_BINARY',0),0o600),'wb') as stream:
        stream.write(raw); stream.flush(); os.fsync(stream.fileno())


def build(api_origin,hub_origin,output,allow_http=False,probe_origin=None,timeout=5,host=None,request=None):
    require(type(timeout) is int and 1<=timeout<=30 and type(allow_http) is bool)
    api=origin(api_origin,allow_http); hub=origin(hub_origin,allow_http)
    probe=origin(probe_origin or api,allow_http,loopback=True)
    output=Path(output).absolute(); ordinary(output.parent); require(not output.exists())
    host_report=(host or H.inspect)(timeout=timeout)
    observation=(request or query)(probe,timeout)
    if observation['status']=='ok': profile(observation['profile'])
    host_ok=host_report['prerequisitesPassed'] is True
    profile_ok=observation['status']=='ok' and observation['profile']['persistentStorageConfigured'] is True
    matched=api==probe
    passed=host_ok and profile_ok and matched
    actions=[]
    if not host_ok: actions.extend(host_report['nextActions'])
    if observation['status']!='ok': actions.append('Run a runtime containing /health/self-hosted and check the selected origin, HTTP acknowledgement and host TLS trust.')
    elif not profile_ok: actions.append('Configure PostgreSql for state and personal memory using the self-hosted runbook.')
    if not matched: actions.append('Probe the selected robot-facing API origin before exporting its conversion target; loopback-only observation is insufficient.')
    actions.extend(['Verify database connectivity, migrations, restart persistence and isolated restore.',
                    'Verify robot reachability, issued-token API/Hub sockets and speech; configuration is not protocol acceptance.'])
    if observation['status']=='ok' and observation['profile']['tokenlessSingleRobotHubConfigured']:
        actions.append('Tokenless compatibility admits one client address only; multi-robot/OOBE tests require issued tokens or separately isolated instances.')
    report=dict(schema='openjibo.self-hosted-readiness.v1',checkedAtUtc=datetime.now(timezone.utc).isoformat(),
                apiOrigin=api,hubOrigin=hub,probeOrigin=probe,allowIsolatedHttp=allow_http,
                host=host_report,runtime=observation,apiOriginMatchesProbe=matched,observationsPassed=passed,targetExported=passed,
                configurationOnly=True,databaseConnectivityVerified=False,migrationsVerified=False,restartPersistenceVerified=False,
                restoreVerified=False,engineToRuntimeAssociationVerified=False,runtimeImageDigestVerified=False,
                robotNetworkingVerified=False,robotProtocolVerified=False,oobeDeploymentVerified=False,repeatDeploymentVerified=False,
                installCertified=False,readyForApply=False,containersStarted=False,deviceWritesPerformed=False,databaseWritesPerformed=False,
                inputTrust='operator-selected-origins-and-runtime-self-report',nextActions=actions,
                codeSha256=dict(reporter=sha(Path(__file__).read_bytes()),hostChecker=sha(Path(H.__file__).read_bytes())))
    output.mkdir(mode=0o700,exist_ok=False)
    if passed:
        target=dict(schema='openjibo.conversion-target.v1',mode='isolated',providerId='self-hosted',apiOrigin=api)
        raw=canonical(target)+b'\n'; write(output/'target.json',raw)
        report['targetSha256']=sha(raw); report['targetCanonicalSha256']=sha(canonical(target))
    escape=lambda value:html.escape(str(value),quote=True)
    rows=''.join('<tr><td>'+escape(row['name'])+'</td><td>'+escape(row['status'])+'</td></tr>' for row in host_report['checks'])
    page=('<!doctype html><html lang="en"><meta charset="utf-8"><title>Self-hosted setup observations</title>'
          '<meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'; base-uri \'none\'; form-action \'none\'">'
          '<style>body{font:16px system-ui;max-width:850px;margin:40px auto;padding:20px}td{padding:8px;border-bottom:1px solid #ddd}li{margin:10px 0}</style>'
          '<h1>Self-hosted setup observations</h1><p>'+('Observations passed' if passed else 'Needs attention')+' · Installation and robot acceptance unverified</p>'
          '<h2>Host prerequisites</h2><table>'+rows+'</table><h2>Runtime configuration</h2><p>API: '+escape(api)+'<br>Hub: '+escape(hub)+'<br>Probe: '+escape(probe)+
          '<br>Profile response: '+escape(observation['status'])+'</p><p>These observations do not test database connectivity or robot protocols.</p>'+
          ('<p><a href="target.json">Generated conversion target</a></p>' if passed else '<p>Conversion target not exported.</p>')+
          '<h2>Next checks</h2><ul>'+''.join('<li>'+escape(action)+'</li>' for action in actions)+'</ul><a href="readiness.json">Report and file hashes</a></html>').encode('utf-8')
    write(output/'readiness.html',page)
    report['files']=[dict(file=p.name,sizeBytes=p.stat().st_size,sha256=sha(p.read_bytes())) for p in sorted(output.iterdir())]
    report['reportComplete']=True
    write(output/'readiness.json',canonical(report)+b'\n')
    return report


def main():
    if len(sys.argv)==4 and sys.argv[1]=='--_probe':
        print(json.dumps(fetch(sys.argv[2],min(30,max(1,int(sys.argv[3])))),sort_keys=True)); return 0
    class Parser(argparse.ArgumentParser):
        def error(self,message): self.exit(2,'Readiness packet refused; see docs/self-hosted-readiness.md.\n')
    parser=Parser(description=__doc__)
    for name in ('api-origin','hub-origin','output'): parser.add_argument('--'+name,required=True,type=Path if name=='output' else str)
    parser.add_argument('--probe-origin')
    parser.add_argument('--allow-isolated-http',action='store_true')
    parser.add_argument('--timeout',type=int,choices=range(1,31),default=5)
    args=parser.parse_args()
    try: report=build(args.api_origin,args.hub_origin,args.output,args.allow_isolated_http,args.probe_origin,args.timeout)
    except (ValueError,OSError,UnicodeError,KeyError,TypeError,RecursionError):
        parser.exit(2,'Readiness packet refused; check explicit origins, isolated HTTP acknowledgement and new output.\n')
    print(json.dumps(dict(reportExported=True,observationsPassed=report['observationsPassed'],targetExported=report['targetExported'],
                          reportSha256=sha(canonical(report)),readyForApply=False),sort_keys=True))
    return 0 if report['observationsPassed'] else 2


if __name__=='__main__': raise SystemExit(main())
