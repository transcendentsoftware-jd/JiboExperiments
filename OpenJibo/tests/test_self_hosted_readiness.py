import copy
import importlib.util
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from unittest.mock import patch
import urllib.error

SPEC=importlib.util.spec_from_file_location('readiness',Path(__file__).parents[1]/'scripts/cloud/prepare-self-hosted-readiness.py')
Q=importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(Q)


def profile():
    return dict(schema='openjibo.self-hosted-configuration.v1',service='OpenJibo Cloud Api',deploymentMode='self-hosted-isolated',
                stateBackend='postgresql',personalMemoryBackend='postgresql',persistentStorageConfigured=True,
                tokenlessSingleRobotHubConfigured=False,configurationOnly=True,databaseConnectivityVerified=False,
                robotCompatibilityVerified=False,readyForApply=False)


def host(system='Linux',machine='x86_64'):
    return Q.H.inspect(system=system,machine=machine,find=lambda _:'docker',
                       run=lambda args,_:('ok','2.30.1' if args[1]=='compose' else '{"Os":"linux","Arch":"amd64"}'))


class Readiness(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name).resolve(); self.output=self.root/'report'

    def build(self,host_report=None,observation=None,**kwargs):
        return Q.build('http://192.168.50.20:8080','http://hub.local:8080',self.output,True,
                       host=lambda **_:host_report or host(),request=lambda *_:observation or dict(status='ok',profile=profile()),**kwargs)

    def test_three_os_reports_export_exact_conversion_target_and_false_acceptance(self):
        for system,machine in [('Windows','AMD64'),('Linux','x86_64'),('Darwin','arm64')]:
            self.output=self.root/system
            value=self.build(host_report=host(system,machine))
            self.assertTrue(value['observationsPassed']); self.assertTrue(value['targetExported'])
            raw=(self.output/'target.json').read_bytes(); target=json.loads(raw)
            self.assertEqual(target,dict(schema='openjibo.conversion-target.v1',mode='isolated',providerId='self-hosted',apiOrigin='http://192.168.50.20:8080'))
            self.assertEqual(Q.sha(raw),value['targetSha256'])
            for field in ('readyForApply','installCertified','databaseConnectivityVerified','migrationsVerified','restartPersistenceVerified',
                          'restoreVerified','engineToRuntimeAssociationVerified','robotNetworkingVerified','robotProtocolVerified',
                          'oobeDeploymentVerified','repeatDeploymentVerified','containersStarted','deviceWritesPerformed','databaseWritesPerformed'):
                self.assertFalse(value[field])
            for item in value['files']:
                self.assertEqual(Q.sha((self.output/item['file']).read_bytes()),item['sha256'])

    def test_missing_host_and_failed_runtime_export_attention_report_without_target(self):
        value=self.build(host_report=Q.H.inspect(find=lambda _:None),observation=dict(status='unavailable',profile=None))
        self.assertTrue(value['reportComplete']); self.assertFalse(value['targetExported'])
        self.assertFalse((self.output/'target.json').exists())
        self.assertIn('Needs attention',(self.output/'readiness.html').read_text())

    def test_loopback_probe_does_not_certify_robot_facing_origin(self):
        value=self.build(probe_origin='http://127.0.0.1:8080')
        self.assertFalse(value['apiOriginMatchesProbe']); self.assertFalse(value['targetExported'])
        self.assertEqual(value['runtime']['status'],'ok')

    def test_http_requires_explicit_acknowledgement_and_private_origin(self):
        for url,allow in [('http://192.168.0.2',False),('http://example.test',True),('http://8.8.8.8',True)]:
            with self.subTest(url=url),self.assertRaises(ValueError): Q.origin(url,allow)
        self.assertEqual(Q.origin('https://api.example.test:443/',False),'https://api.example.test')
        self.assertEqual(Q.origin('http://[fd00::1]:8080',True),'http://[fd00::1]:8080')

    def test_unsafe_urls_credentials_paths_and_loopback_targets_refused(self):
        for url in ('https://user:password@example.test','http://127.0.0.1','https://[::1]','https://localhost',
                    'https://example.test/path','https://example.test?token=secret','https://example.test#secret',
                    'ftp://example.test','https://example.test:0','https://example.test:99999','https://2130706433',
                    'https://example.test/\nsecret','https://☃.test','https://example.test\\evil'):
            with self.subTest(url=url),self.assertRaises(ValueError): Q.origin(url,True)

    def test_sqlite_profile_is_observed_but_not_postgresql_starter_readiness(self):
        data=profile(); data.update(stateBackend='sqlite',persistentStorageConfigured=False)
        value=self.build(observation=dict(status='ok',profile=data))
        self.assertFalse(value['observationsPassed']); self.assertFalse(value['targetExported'])

    def test_positive_or_wrong_type_runtime_claim_cannot_promote_acceptance(self):
        for key,value in [('readyForApply',True),('databaseConnectivityVerified',True),('persistentStorageConfigured',1),
                          ('service','PRIVATE_RESPONSE'),('deploymentMode','managed')]:
            data=profile(); data[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError): Q.profile(data)

    def test_tokenless_single_client_limit_is_visible_for_the_multi_robot_lab(self):
        data=profile(); data['tokenlessSingleRobotHubConfigured']=True
        value=self.build(observation=dict(status='ok',profile=data))
        self.assertTrue(any('one client address' in action for action in value['nextActions']))
        self.assertFalse(value['robotProtocolVerified'])

    def test_existing_output_refused_before_docker_or_http(self):
        self.output.mkdir(); (self.output/'keep').write_bytes(b'keep')
        with patch.object(Q.H,'inspect',side_effect=AssertionError('no host call')),self.assertRaises(ValueError):
            Q.build('https://api.example.test','https://hub.example.test',self.output)
        self.assertEqual((self.output/'keep').read_bytes(),b'keep')

    def server(self,body=None,status=200,delay=0):
        requests=[]
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*args): pass
            def do_GET(self):
                requests.append(self.path)
                if delay: time.sleep(delay)
                self.send_response(status); self.send_header('Content-Type','application/json')
                if status==302: self.send_header('Location','/private-secret')
                self.end_headers()
                try: self.wfile.write(body if body is not None else json.dumps(profile()).encode())
                except OSError: pass
        server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True); thread.start()
        def stop(): server.shutdown(); server.server_close(); thread.join(timeout=2)
        self.addCleanup(stop)
        return 'http://127.0.0.1:'+str(server.server_address[1]),requests

    def test_real_supervised_http_probe_uses_only_fixed_read_only_path(self):
        url,requests=self.server()
        value=Q.query(url,5)
        self.assertEqual(value,dict(status='ok',profile=profile()))
        self.assertEqual(requests,['/health/self-hosted'])

    def test_redirect_and_sensitive_invalid_or_oversized_json_are_not_exported(self):
        for body,status in [(None,302),(b'{"password":"PRIVATE_RESPONSE"}',200),(b'x'*8193,200),
                            (b'{"schema":"PRIVATE_RESPONSE","schema":"duplicate"}',200)]:
            url,requests=self.server(body,status)
            value=Q.query(url,5)
            self.assertNotEqual(value['status'],'ok'); self.assertIsNone(value['profile'])
            self.assertNotIn('PRIVATE_RESPONSE',json.dumps(value))
            self.assertEqual(requests,['/health/self-hosted'])

    def test_slow_http_child_is_killed_and_reaped_at_parent_deadline(self):
        url,_=self.server(delay=10)
        started=time.monotonic(); value=Q.query(url,1)
        self.assertEqual(value,dict(status='timeout',profile=None))
        self.assertLess(time.monotonic()-started,5)

    def test_tls_failure_is_classified_without_exception_or_certificate_contents(self):
        error=urllib.error.URLError(ssl.SSLError('PRIVATE_CERTIFICATE'))
        with patch.object(Q.urllib.request.OpenerDirector,'open',side_effect=error): value=Q.fetch('https://api.example.test',2)
        self.assertEqual(value,dict(status='tls-error',profile=None))

    def test_cli_without_docker_exports_redacted_attention_report_outside_checkout(self):
        url,_=self.server()
        env=os.environ.copy(); env['PATH']=''
        command=[sys.executable,'-I','-B',str(Path(Q.__file__)),'--api-origin','http://192.168.50.20:8080',
                 '--hub-origin','http://hub.local:8080','--probe-origin',url,'--allow-isolated-http','--output',str(self.output)]
        run=subprocess.run(command,cwd=self.root,env=env,capture_output=True,timeout=15)
        self.assertEqual(run.returncode,2,run.stderr)
        summary=json.loads(run.stdout)
        self.assertTrue(summary['reportExported']); self.assertFalse(summary['targetExported'])
        self.assertNotIn(str(self.root).encode(),run.stdout+run.stderr)
        self.assertTrue((self.output/'readiness.json').is_file())


if __name__=='__main__': unittest.main()
