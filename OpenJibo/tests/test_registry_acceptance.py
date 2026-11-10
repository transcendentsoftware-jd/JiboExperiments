import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

DIRECTORY=Path(__file__).parents[1]/'scripts/cloud'
sys.path.insert(0,str(DIRECTORY))
import registry_acceptance as R
SPEC=importlib.util.spec_from_file_location('registry_image',DIRECTORY/'check-registry-acceptance-image.py')
I=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(I)


class AcceptanceBoundaryTests(unittest.TestCase):
    def test_only_loopback_or_exact_isolated_staging_origin(self):
        staging='registry-fixture.example.invalid'
        self.assertIsNotNone(R.origin('http://127.0.0.1:12345'))
        self.assertIsNotNone(R.origin('https://'+staging,staging))
        for bad in ('http://192.168.130.100:8080','http://172.24.84.101:8080','https://api.openjibo.com',
                    'http://localhost:8080','http://127.0.0.1:1234/','http://user:pass@127.0.0.1:1234',
                    'https://'+staging+'/path','https://'+staging+'?query=1',
                    'https://'+staging+'.example.invalid','http://'+staging+':80'):
            with self.subTest(bad=bad),self.assertRaises(R.Refusal):R.origin(bad,staging)
        with self.assertRaises(R.Refusal):R.origin('https://'+staging)

    def test_rotations_change_only_the_intended_inventory_and_keep_encryption(self):
        f=R.Fixture();phases=[f.environment(p,'owned-pg') for p in ('baseline','removed','rotated')]
        self.assertIn(f.secret_a,phases[0].values());self.assertNotIn(f.secret_a,phases[1].values());self.assertNotIn(f.id_a,phases[1].values())
        self.assertIn(f.secret_b,phases[1].values());self.assertNotIn(f.secret_a,phases[2].values());self.assertIn(f.rotated_a,phases[2].values())
        for env in phases:
            self.assertEqual(f.encryption,env['OPENJIBO_USER_ENCRYPT']);self.assertEqual(f.salt,env['OPENJIBO_USER_SALT'])
            self.assertNotIn(f.admin_password,json.dumps(env));self.assertEqual('',env['OpenJibo__Weather__OpenWeather__ApiKey'])
            self.assertIn('registry_runtime',env['OpenJibo__State__ConnectionString'])
        self.assertNotIn(f.secret_a,repr(f));self.assertNotIn(f.secret_b,repr(f))

    def test_fixed_fresh_database_names_and_nonprivileged_runtime(self):
        f=R.Fixture();sql=f.init_sql()
        self.assertIn('NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION',sql)
        self.assertIn('registry_state',sql);self.assertIn('registry_memory',sql)
        for name in ('production','openjibo_cloud','postgres'):
            with self.subTest(name=name),self.assertRaises(R.Refusal):f.connection('owned-pg',name)
        f.runtime_password="unsafe'password"
        with self.assertRaises(R.Refusal):f.init_sql()

    def test_no_token_post_retry_or_response_body_in_diagnostic(self):
        f=R.Fixture();client=R.Acceptance(f,'http://127.0.0.1:1234')
        with patch.object(client,'http',return_value=(503,b'private-response')) as call:
            with self.assertRaises(R.Refusal) as error:client.issue('one',f.id_a,f.secret_a,f.device_a)
        self.assertEqual(1,call.call_count);self.assertNotIn('private-response',str(error.exception));self.assertEqual(1,client.posts)

    def test_post_budget_and_bad_token_refusal_before_connect(self):
        f=R.Fixture();client=R.Acceptance(f,'http://127.0.0.1:1234');client.posts=12
        with patch.object(client,'http') as call:
            with self.assertRaises(R.Refusal):client.issue('over-budget',f.id_a,f.secret_a,f.device_a)
        call.assert_not_called()
        for token in ('x\r\nInjected: true','a/b','', 'a'*2049):
            with self.subTest(tokenLength=len(token)),patch.object(R.socket,'create_connection') as connect:
                with self.assertRaises(R.Refusal):client.websocket('bad',token,'listen',101)
                connect.assert_not_called()

    def test_token_response_requires_a_bounded_safe_bearer(self):
        for response in (b'{}',b'{"token":null}',b'{"token":"a/b"}',b'{"token":"a\\nheader"}'):
            f=R.Fixture();client=R.Acceptance(f,'http://127.0.0.1:1234')
            with self.subTest(response=response),patch.object(client,'http',return_value=(200,response)):
                with self.assertRaises(R.Refusal):client.issue('bad-response',f.id_a,f.secret_a,f.device_a)

    def test_cleanup_refuses_an_unowned_container(self):
        f=R.Fixture()
        with tempfile.TemporaryDirectory() as tmp:
            controller=I.DockerFixture('sha256:'+'a'*64,f,Path(tmp))
            with patch.object(I,'command',return_value=json.dumps([{'Image':controller.image,'Config':{'Labels':{'openjibo.test.scope':'other'}}}])):
                with self.assertRaises(R.Refusal):controller.owned('unowned',controller.image)

    def test_cleanup_recovers_an_ambiguous_network_create(self):
        f=R.Fixture()
        with tempfile.TemporaryDirectory() as tmp:
            controller=I.DockerFixture('sha256:'+'a'*64,f,Path(tmp));controller.network_created=True
            found=type('Result',(),{'returncode':0,'stdout':json.dumps([{'Labels':{'openjibo.test.scope':f.scope},'Containers':{}}]).encode()})()
            with patch.object(I.subprocess,'run',return_value=found),patch.object(I,'command',return_value='') as command:
                controller.cleanup()
            self.assertEqual(['docker','network','rm',controller.network],command.call_args.args[0])

    def test_database_restart_refuses_further_api_mutations(self):
        f=R.Fixture()
        with tempfile.TemporaryDirectory() as tmp:
            controller=I.DockerFixture('sha256:'+'a'*64,f,Path(tmp));controller.pg_started='before'
            with patch.object(controller,'owned',return_value={'State':{'StartedAt':'after'}}),patch.object(I,'command') as command:
                with self.assertRaises(R.Refusal):controller.start_api('rotated')
                command.assert_not_called()


if __name__=='__main__':unittest.main()
