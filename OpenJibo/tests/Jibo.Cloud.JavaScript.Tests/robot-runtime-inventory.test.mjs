import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { inspect } = require('../../scripts/bootstrap/inspect-robot-runtime.js');
const root = '/usr/local/etc/';
function fixture(files, grow = false) {
    const opened = [], closed = [];
    const io = {
        statSync: p => { if (!(p in files)) throw Error('missing'); return { isFile: () => true }; },
        openSync: (p, mode) => { assert.equal(mode, 'r'); if (!(p in files)) throw Error('missing'); opened.push(p); return { p, at: 0 }; },
        fstatSync: fd => ({ isFile: () => true, size: grow ? 1 : Buffer.byteLength(files[fd.p]) }),
        readSync: (fd, buffer, offset, length) => {
            const bytes = Buffer.from(files[fd.p]); const n = Math.min(length, bytes.length - fd.at, 113);
            bytes.copy(buffer, offset, fd.at, fd.at + n); fd.at += n; return n;
        },
        closeSync: fd => closed.push(fd.p)
    };
    return { result: inspect(io), opened, closed };
}
test('configured robot reports package version and candidates without claiming readiness or reading credentials', () => {
    const f = fixture({
        '/usr/local/bin/jibo-ssm/package.json': JSON.stringify({ version: '14.2.9', secret: 'never-export' }),
        [root+'jibo-jetstream-service.json']: JSON.stringify({ HubClient: {
            xxx_override: { hub_hostname: 'unused.invalid' }, 'region-settings': {
                api: { entrypoint_hostname: 'api.jibo.com', hub_hostname: 'neo-hub.jibo.com', password: 'never-export' },
                'open-jibo': { entrypoint_hostname: 'api.example.test', hub_hostname: 'hub.example.test' }
            } }, credentials: 'never-export' }),
        [root+'jibo-server-service.json']: JSON.stringify({ NotificationSubsystem: { serverURLSuffix: '-socket.example.test', token: 'never-export' } }),
        '/var/jibo/credentials.json': '{"secret":"never-open"}'
    });
    assert.equal(f.result.components.systemManagerPackageVersion, '14.2.9');
    assert.equal(f.result.configuration.regionCandidates.length, 2);
    assert.equal(f.result.files.credentialsFilePresent, true);
    for (const flag of ['firmwareReleaseVerified', 'activeRoutingVerified', 'credentialIdentityVerified', 'conversionReady', 'credentialsRead', 'writesPerformed']) assert.equal(f.result[flag], false);
    assert.ok(!f.opened.includes('/var/jibo/credentials.json'));
    assert.deepEqual(f.opened, f.closed);
    assert.doesNotMatch(JSON.stringify(f.result), /never-|unused\.invalid/);
});
test('missing OOBE files do not invent a firmware release, region or credentials', () => {
    const { result } = fixture({});
    assert.equal(result.components.systemManagerPackageVersion, null);
    assert.deepEqual(result.configuration.regionCandidates, []);
    assert.equal(result.files.credentialsFilePresent, false);
    assert.equal(result.conversionReady, false);
});
test('malformed and oversized configuration stay unknown and close descriptors', () => {
    for (const content of ['not json', 'x'.repeat(65537), '{}'.repeat(40000)]) {
        const f = fixture({ [root+'jibo-jetstream-service.json']: content });
        assert.deepEqual(f.result.configuration.regionCandidates, []);
        assert.deepEqual(f.opened, f.closed);
    }
});
test('a growing file cannot exceed the bounded read', () => {
    const f = fixture({ [root+'jibo-jetstream-service.json']: ' '.repeat(70000) }, true);
    assert.deepEqual(f.result.configuration.regionCandidates, []);
    assert.deepEqual(f.opened, f.closed);
});
test('credential-bearing URL values and arbitrary strings are not exported as hostnames or versions', () => {
    const { result } = fixture({
        '/usr/local/bin/jibo-ssm/package.json': '{"version":"private-value"}',
        [root+'jibo-jetstream-service.json']: JSON.stringify({ HubClient: { 'region-settings': { api: {
            entrypoint_hostname: 'https://user:never-export@example.test/path', hub_hostname: 'private value'
        } } } }),
        [root+'jibo-server-service.json']: '{"NotificationSubsystem":{"serverURLSuffix":"https://never-export"}}'
    });
    assert.equal(result.components.systemManagerPackageVersion, null);
    assert.equal(result.configuration.regionCandidates[0].apiHostname, null);
    assert.equal(result.configuration.regionCandidates[0].hubHostname, null);
    assert.equal(result.configuration.notificationHostnameSuffix, null);
    assert.doesNotMatch(JSON.stringify(result), /never-export|private value/);
});
