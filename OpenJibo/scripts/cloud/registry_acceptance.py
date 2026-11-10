"""Synthetic-only registry acceptance shared by owned image/staging controllers.

No arbitrary credential input, redirects, robot hosts, audio or provider calls.
Only fixed protocol token operations and fresh WebSocket handshakes are sent.
"""
import base64
import hashlib
import hmac
import http.client
import ipaddress
import json
import re
import secrets
import socket
import ssl
import time
import urllib.parse
import uuid
from datetime import datetime, timezone

POSTGRES_IMAGE = 'postgres@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea'


class Refusal(RuntimeError):
    """Fixed diagnostic identifiers only; never format credentials or responses."""


def origin(value, staging_host=None):
    parsed = urllib.parse.urlsplit(value)
    if parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path:
        raise Refusal('origin-shape-refused')
    if parsed.scheme == 'http' and parsed.hostname == '127.0.0.1' and parsed.port:
        return parsed
    if staging_host and parsed.scheme=='https' and parsed.hostname==staging_host and parsed.port in (None,443):
        try: ipaddress.ip_address(staging_host)
        except ValueError:
            if '.' in staging_host and re.fullmatch('[a-z0-9.-]+',staging_host) and not staging_host.endswith(('.local','.localhost','.internal')):
                return parsed
    raise Refusal('origin-not-owned-fixture')


class Fixture:
    def __init__(self):
        self.scope = uuid.uuid4().hex
        self.id_a = 'registry-fixture-a-' + self.scope[:12]
        self.id_b = 'registry-fixture-b-' + self.scope[:12]
        self.device_a = 'synthetic-registry-a-' + self.scope[:12]
        self.device_b = 'synthetic-registry-b-' + self.scope[:12]
        self.secret_a, self.secret_b, self.rotated_a = [secrets.token_urlsafe(24) for _ in range(3)]
        self.admin_password, self.runtime_password = [secrets.token_urlsafe(24) for _ in range(2)]
        self.evidence_key = secrets.token_urlsafe(32)
        self.encryption, self.salt = secrets.token_urlsafe(32), secrets.token_urlsafe(32)

    def __repr__(self):
        return 'Fixture(redacted)'

    def connection(self, host, database, admin=False):
        if database not in ('registry_state', 'registry_memory') or not re.fullmatch('[a-z0-9.-]+', host):
            raise Refusal('database-binding-refused')
        user, password = ('fixture_admin', self.admin_password) if admin else ('registry_runtime', self.runtime_password)
        return f'Host={host};Port=5432;Database={database};Username={user};Password={password};SSL Mode=Disable;Maximum Pool Size=4'

    def init_sql(self):
        if not re.fullmatch('[A-Za-z0-9_-]{32}', self.runtime_password):
            raise Refusal('generated-password-shape-refused')
        statements = [f"CREATE ROLE registry_runtime LOGIN PASSWORD '{self.runtime_password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;",
                      'CREATE DATABASE registry_memory OWNER fixture_admin;',
                      'REVOKE ALL ON DATABASE postgres FROM PUBLIC;',
                      'REVOKE ALL ON DATABASE template1 FROM PUBLIC;']
        for database in ('registry_state', 'registry_memory'):
            statements += [f'\\connect {database}',
                f'REVOKE ALL ON DATABASE {database} FROM PUBLIC;',
                f'GRANT CONNECT ON DATABASE {database} TO registry_runtime;',
                'REVOKE CREATE ON SCHEMA public FROM PUBLIC;',
                'GRANT USAGE ON SCHEMA public TO registry_runtime;',
                'ALTER DEFAULT PRIVILEGES FOR ROLE fixture_admin IN SCHEMA public GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO registry_runtime;',
                'ALTER DEFAULT PRIVILEGES FOR ROLE fixture_admin IN SCHEMA public GRANT USAGE,SELECT ON SEQUENCES TO registry_runtime;']
        return '\n'.join(statements) + '\n'

    def environment(self, phase, database_host, managed=False, api_host='127.0.0.1'):
        if phase not in ('baseline', 'removed', 'rotated'):
            raise Refusal('phase-refused')
        values = {
            'ASPNETCORE_URLS': 'http://+:8080', 'DOTNET_EnableDiagnostics': '0',
            'OpenJibo__Deployment__Mode': 'managed' if managed else 'self-hosted-isolated',
            'OpenJibo__Security__Enabled': 'true' if managed else 'false',
            'OpenJibo__Testing__SkipSttDependencyValidation': 'true',
            'OpenJibo__Stt__EnableLocalWhisperCpp': 'false', 'OpenJibo__Stt__EnableAzureSpeech': 'false',
            'OpenJibo__Stt__EnableWhisperServer': 'false',
            'OpenJibo__State__Backend': 'PostgreSql', 'OpenJibo__PersonalMemory__Backend': 'PostgreSql',
            'OpenJibo__State__ConnectionString': self.connection(database_host, 'registry_state'),
            'OpenJibo__PersonalMemory__ConnectionString': self.connection(database_host, 'registry_memory'),
            'OpenJibo__Media__Backend': 'File', 'OpenJibo__Media__DirectoryPath': '/tmp/registry-media',
            'OpenJibo__Logging__DirectoryPath': '/tmp/registry-logs', 'OpenJibo__Logging__MinimumLevel': 'Warning',
            'Logging__LogLevel__Default': 'Warning', 'OpenJibo__Logging__ResetOnStartup': 'false',
            'OpenJibo__Telemetry__Enabled': 'false', 'OpenJibo__Telemetry__ExportFixtures': 'false',
            'OpenJibo__ProtocolTelemetry__Enabled': 'false', 'OpenJibo__TurnTelemetry__Enabled': 'false',
            'OpenJibo__Weather__OpenWeather__ApiKey': '', 'OpenJibo__Weather__OpenWeather__BaseUrl': 'http://127.0.0.1:9',
            'OpenJibo__AcceptedHosts__0': api_host,
            'OpenJibo__ManagedPairingCredentialEvidence__Enabled': 'true',
            'OpenJibo__ManagedPairingCredentialEvidence__HmacKey': self.evidence_key,
            'OPENJIBO_USER_ENCRYPT': self.encryption, 'OPENJIBO_USER_SALT': self.salt,
            'OpenJibo__Security__TrustedProtocolCredentials__Enabled': 'true',
        }
        credentials = [(self.id_b, self.secret_b)] if phase == 'removed' else [
            (self.id_a, self.rotated_a if phase == 'rotated' else self.secret_a), (self.id_b, self.secret_b)]
        for index, (identity, secret) in enumerate(credentials):
            prefix = 'OpenJibo__Security__TrustedProtocolCredentials__Credentials__' + str(index)
            values[prefix+'__AccessKeyId'] = identity
            values[prefix+'__SecretAccessKey'] = secret
        return values


def signed_headers(host, access_id, secret, device, hub=True):
    now = datetime.now(timezone.utc); timestamp = now.strftime('%Y%m%dT%H%M%SZ'); date = timestamp[:8]
    target = 'Account_20151111.CreateHubToken' if hub else 'Notification_20150505.NewRobotToken'
    region = 'api' if hub else 'us-east-1'
    body = json.dumps({'deviceId': device}, separators=(',', ':')).encode()
    digest = hashlib.sha256(body).hexdigest()
    headers = {'host': host, 'x-amz-content-sha256': digest, 'x-amz-date': timestamp, 'x-amz-target': target}
    names = ';'.join(sorted(headers))
    canonical_headers = ''.join(name+':'+headers[name]+'\n' for name in sorted(headers))
    canonical = 'POST\n/\n\n'+canonical_headers+'\n'+names+'\n'+digest
    scope = date+'/'+region+'/jibo/aws4_request'
    message = 'AWS4-HMAC-SHA256\n'+timestamp+'\n'+scope+'\n'+hashlib.sha256(canonical.encode()).hexdigest()
    key = ('AWS4'+secret).encode()
    for value in (date, region, 'jibo', 'aws4_request'):
        key = hmac.new(key, value.encode(), hashlib.sha256).digest()
    signature = hmac.new(key, message.encode(), hashlib.sha256).hexdigest()
    headers['Authorization'] = 'AWS4-HMAC-SHA256 Credential='+access_id+'/'+scope+', SignedHeaders='+names+', Signature='+signature
    headers['Content-Type'] = 'application/x-amz-json-1.1'
    return headers, body


class Acceptance:
    def __init__(self, fixture, base, staging_host=None):
        self.fixture, self.base = fixture, base
        self.staging_host=staging_host
        self.parsed = origin(base,staging_host)
        self.host = self.parsed.netloc
        self.results, self.tokens = [], {}
        self.posts = 0

    def __repr__(self):
        return 'Acceptance(redacted)'

    def set_origin(self, value):
        parsed = origin(value,self.staging_host)
        self.base, self.parsed, self.host = value, parsed, parsed.netloc

    def http(self, method, headers=None, body=None):
        connection = (http.client.HTTPSConnection if self.parsed.scheme == 'https' else http.client.HTTPConnection)(
            self.parsed.hostname, self.parsed.port, timeout=4)
        try:
            connection.request(method, '/' if method == 'POST' else '/health', body=body, headers=headers or {})
            response = connection.getresponse()
            data = response.read(4097)
            if len(data) > 4096: raise Refusal('http-body-limit')
            return response.status, data
        finally:
            connection.close()

    def ready(self):
        deadline=time.monotonic()+90
        while time.monotonic()<deadline:
            try:
                if self.http('GET')[0] == 200: return
            except (OSError, http.client.HTTPException): pass
            time.sleep(1)
        raise Refusal('readiness-deadline')

    def issue(self, label, identity, secret, device, expected=200, hub=True):
        self.posts += 1
        if self.posts > 12: raise Refusal('post-budget')
        headers, body = signed_headers(self.host, identity, secret, device, hub)
        actual, response = self.http('POST', headers, body)
        self.results.append({'case':label, 'expected':expected, 'actual':actual})
        if actual != expected: raise Refusal('token-status-unexpected')
        if expected != 200: return None
        try: token = json.loads(response)['token']
        except (ValueError, KeyError, TypeError): raise Refusal('token-response-refused') from None
        if not isinstance(token,str) or not 1 <= len(token) <= 2048 or not re.fullmatch('[A-Za-z0-9_-]+', token):
            raise Refusal('token-shape-refused')
        return token

    def websocket(self, label, token, channel, expected):
        if channel not in ('listen','proactive','notification'): raise Refusal('channel-refused')
        if not isinstance(token,str) or not 1<=len(token)<=2048 or not re.fullmatch('[A-Za-z0-9_-]+',token):
            raise Refusal('websocket-token-shape')
        path = '/listen' if channel == 'listen' else '/v1/proactive' if channel == 'proactive' else '/'+token
        nonce = base64.b64encode(secrets.token_bytes(16)).decode()
        raw = socket.create_connection((self.parsed.hostname,self.parsed.port or (443 if self.parsed.scheme=='https' else 80)), timeout=4)
        try:
            if self.parsed.scheme=='https': raw = ssl.create_default_context().wrap_socket(raw,server_hostname=self.parsed.hostname)
            request = f'GET {path} HTTP/1.1\r\nHost: {self.host}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: {nonce}\r\nAuthorization: Bearer {token}\r\n\r\n'
            raw.sendall(request.encode()); data = bytearray()
            while not data.endswith(b'\r\n\r\n'):
                chunk = raw.recv(1)
                if not chunk or len(data) >= 8192: raise Refusal('websocket-header-limit')
                data.extend(chunk)
            lines = bytes(data).decode('ascii').split('\r\n'); actual = int(lines[0].split(' ')[1])
            self.results.append({'case':label, 'expected':expected, 'actual':actual})
            if actual != expected: raise Refusal('websocket-status-unexpected')
            if actual == 101:
                accept = base64.b64encode(hashlib.sha1((nonce+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
                fields = dict(line.split(': ',1) for line in lines[1:] if ': ' in line)
                if not any(k.lower()=='sec-websocket-accept' and v==accept for k,v in fields.items()):
                    raise Refusal('websocket-accept-refused')
                raw.sendall(b'\x88\x80'+secrets.token_bytes(4))
        finally: raw.close()

    def channels(self, label, hub_token, robot_token, status):
        for channel in ('listen','proactive','notification'):
            self.websocket(label+'-'+channel, robot_token if channel=='notification' else hub_token, channel, status)

    def baseline(self):
        f=self.fixture
        for name,identity,secret,device in [('a',f.id_a,f.secret_a,f.device_a),('b',f.id_b,f.secret_b,f.device_b)]:
            self.tokens[name+'-hub']=self.issue('baseline-'+name+'-hub',identity,secret,device)
            self.tokens[name+'-robot']=self.issue('baseline-'+name+'-robot',identity,secret,device,hub=False)
            self.channels('baseline-'+name,self.tokens[name+'-hub'],self.tokens[name+'-robot'],101)
        self.issue('unknown-credential',f.id_a+'-unknown',f.secret_a,f.device_a,401)
        self.issue('wrong-secret',f.id_a,f.secret_b,f.device_a,401)
        self.posts+=1
        actual,_=self.http('POST',{'Content-Type':'application/x-amz-json-1.1','x-amz-target':'Account_20151111.CreateHubToken'},b'{}')
        self.results.append({'case':'unsigned-token','expected':401,'actual':actual})
        if actual!=401: raise Refusal('unsigned-token-admitted')

    def removed(self):
        f=self.fixture
        self.issue('removed-a-signature',f.id_a,f.secret_a,f.device_a,401)
        self.issue('unchanged-b-signature',f.id_b,f.secret_b,f.device_b)
        self.channels('removed-a-old-token',self.tokens['a-hub'],self.tokens['a-robot'],401)
        self.channels('unchanged-b-old-token',self.tokens['b-hub'],self.tokens['b-robot'],101)

    def rotated(self):
        f=self.fixture
        self.issue('rotated-old-a-signature',f.id_a,f.secret_a,f.device_a,401)
        hub=self.issue('rotated-new-a-hub',f.id_a,f.rotated_a,f.device_a)
        robot=self.issue('rotated-new-a-robot',f.id_a,f.rotated_a,f.device_a,hub=False)
        self.channels('rotated-old-a-token',self.tokens['a-hub'],self.tokens['a-robot'],401)
        self.channels('rotated-new-a-token',hub,robot,101)
        self.channels('preserved-b-old-token',self.tokens['b-hub'],self.tokens['b-robot'],101)
