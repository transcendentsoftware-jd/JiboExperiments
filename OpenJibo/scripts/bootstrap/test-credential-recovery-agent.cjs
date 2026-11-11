'use strict';
var fs = require('fs'), path = require('path'), os = require('os'), cp = require('child_process'), assert = require('assert'), crypto = require('crypto'), net = require('net');
var agent = fs.readFileSync(path.join(__dirname, 'credential-recovery-agent.js')), tx = fs.readFileSync(path.join(__dirname, 'credential-delivery-transaction.js'));
var consumerPath = path.join(__dirname, 'fixtures/credential-agent-consumer.js'), consumer = fs.readFileSync(consumerPath), groups = [];
var before = Buffer.from('{"accessKeyId":"fixture-old","secretAccessKey":"old-fixture-secret-0123456789","region":"api"}\n');
var after = Buffer.from('{"accessKeyId":"fixture-new","secretAccessKey":"new-fixture-secret-9876543210","region":"api"}\n');
function sha(b) { return crypto.createHash('sha256').update(b).digest('hex'); }
function delay(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }
function write(file, v) { fs.writeFileSync(file, Buffer.isBuffer(v) ? v : JSON.stringify(v)+'\n', { mode: 384 }); }
function poll(fn, ms) { var end = Date.now()+(ms || 5000); return new Promise(function (resolve, reject) { function next() { try { if (fn()) return resolve(); if (Date.now() >= end) return reject(new Error('Fixture poll expired.')); setTimeout(next, 20); } catch (e) { reject(e); } } next(); }); }
function clean(dir) { fs.readdirSync(dir).forEach(function (name) { var p = path.join(dir, name); if (fs.lstatSync(p).isDirectory()) { clean(p); fs.rmdirSync(p); } else fs.unlinkSync(p); }); }
function check(name, run) { groups.push({ name: name, run: run }); }
function state(f, action) {
    return new Promise(function (resolve, reject) {
        var socket = net.connect(path.join(f.root, 'consumer.sock')), raw = '';
        socket.on('error', reject); socket.on('connect', function () { socket.end(JSON.stringify({ action: action || 'state' })+'\n'); });
        socket.on('data', function (b) { raw += b; }); socket.on('end', function () { try { resolve(JSON.parse(raw).state); } catch (e) { reject(e); } });
    });
}
function fixture(run, options) {
    options = options || {};
    var root = fs.mkdtempSync(path.join(os.tmpdir(), 'cra-')), id = crypto.randomBytes(16).toString('hex'), helper = path.join(root, '.openjibo-delivery-'+id);
    fs.chmodSync(root, 448); fs.mkdirSync(helper, 448);
    write(path.join(root, 'credentials.json'), before); var stock = process.getuid() === 0;
    if (stock) fs.chmodSync(path.join(root, 'credentials.json'), 438);
    write(path.join(helper, 'driver.js'), agent); write(path.join(helper, 'transaction.js'), tx); write(path.join(helper, 'consumer-profile.js'), consumer);
    write(path.join(root, 'fixture-options.json'), options);
    var binding = { operationId: id, hostKeySha256: 'SHA256:'+'A'.repeat(43), beforeSha256: sha(before), afterSha256: sha(after), recoverySha256: 'a'.repeat(64),
        identity: { robotId: '11111111-1111-1111-1111-111111111111', assignmentId: '22222222-2222-2222-2222-222222222222', pairingId: '33333333-3333-3333-3333-333333333333', deviceKeyId: '44444444-4444-4444-4444-444444444444', credentialVersion: 1 },
        associationSha256: 'b'.repeat(64), consumerProfileSha256: sha(consumer), driverSha256: sha(agent), transactionSha256: sha(tx), scenario: 1 };
    var config = { schema: 'openjibo.credential-agent-config.v1', binding: binding, credentialDirectory: root, transactionProfile: stock ? 'stock-root-0666-to-0600' : 'private-0600', watchdogMs: options.watchdogMs || 2000, actionTimeoutMs: options.actionTimeoutMs || 700 };
    write(path.join(helper, 'agent.json'), config);
    var server = cp.spawn(process.execPath, [consumerPath, root], { stdio: ['ignore','pipe','pipe'] }), ready = false, errors = '';
    server.stdout.on('data', function (b) { if (String(b) === 'ready\n') ready = true; }); server.stderr.on('data', function (b) { errors += b; });
    var f = { root: root, helper: helper, binding: binding, config: config, stock: stock, server: server };
    return poll(function () { return ready; }).then(function () { return run(f); }).then(function () { return finish(); }, function (e) { return finish().then(function () { throw e; }); });
    function finish() {
        // Only the explicitly created fixture processes/files are cleaned.
        fs.readdirSync(root).filter(function (name) { return /^\.openjibo-delivery-[a-f0-9]{32}$/.test(name); }).forEach(function (name) {
            var ownHelper = path.join(root, name), daemon = path.join(ownHelper, 'daemon-started.json');
            if (fs.existsSync(daemon)) {
                var pid = JSON.parse(fs.readFileSync(daemon)).pid;
                try { var command = fs.readFileSync('/proc/'+pid+'/cmdline').toString(); if (command.indexOf(path.join(ownHelper, 'driver.js')+'\u0000--daemon') >= 0) process.kill(pid, 'SIGKILL'); } catch (_) {}
            }
        });
        server.kill('SIGKILL');
        return delay(100).then(function () { assert.strictEqual(path.dirname(root), os.tmpdir()); assert.ok(/^cra-[a-zA-Z0-9]+$/.test(path.basename(root))); clean(root); fs.rmdirSync(root); assert.strictEqual(errors, ''); });
    }
}
function request(f, phase) { return { schema: 'openjibo.owned-delivery-rpc.v1', phase: phase, binding: f.binding, replacement: phase === 'prepare' ? after.toString('base64') : null, requestNonce: crypto.randomBytes(16).toString('hex'), expiresAtUnixMs: Date.now()+1900 }; }
function invoke(f, phase, alter) {
    var r = request(f, phase); if (alter) alter(r);
    return cp.spawnSync(process.execPath, [path.join(f.helper, 'driver.js')], { input: JSON.stringify(r)+'\n', encoding: 'utf8', timeout: 4500 });
}
function ok(f, phase, expected) { var r = invoke(f, phase); assert.strictEqual(r.status, 0, r.stderr); assert.strictEqual(r.stderr, ''); var reply = JSON.parse(r.stdout); assert.strictEqual(reply.phase, phase); assert.strictEqual(reply.state, expected); assert.deepStrictEqual(reply.binding, f.binding); return reply; }
function refused(r) { assert.strictEqual(r.status, 2, r.stderr); assert.strictEqual(r.stdout, ''); assert.strictEqual(r.stderr, 'Credential recovery agent refused.\n'); }
function prepare(f) { ok(f, 'inspect', 'qualified'); ok(f, 'prepare', 'prepared'); ok(f, 'arm', 'recovery-armed'); }
function original(f) { assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(before)); assert.strictEqual(fs.statSync(path.join(f.root, 'credentials.json')).mode & 511, f.stock ? 438 : 384); }
check('canonical request and platform refusal', function () {
    var r = cp.spawnSync(process.execPath, [path.join(__dirname, 'credential-recovery-agent.js')], { input: '{}\n', encoding: 'utf8' }); refused(r);
});
if (process.platform === 'win32') { groups[0].run(); console.log(JSON.stringify({ groupsPassed: 1, posixAcceptance: false, windowsMutationRefused: true })); process.exit(0); }
check('resident consumer observes new credential and exact original after recovery', function () { return fixture(function (f) {
    prepare(f); original(f); ok(f, 'activate', 'installed'); assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(after));
    ok(f, 'reload', 'reload-started');
    return state(f).then(function (s) { assert.strictEqual(s.hash, sha(after)); assert.strictEqual(s.newReloads, 1); ok(f, 'fence', 'forward-fenced'); ok(f, 'restore', 'restored'); original(f); return state(f); }).then(function (s) {
        assert.strictEqual(s.hash, sha(before)); assert.strictEqual(s.originalReloads, 1); ok(f, 'restore', 'restored'); return state(f);
    }).then(function (s) { assert.strictEqual(s.originalReloads, 1); refused(invoke(f, 'activate')); });
}, { watchdogMs: 6000 }); });
check('controller exit leaves detached watchdog restoring both file and consumer', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); ok(f, 'reload', 'reload-started');
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); }).then(function () { original(f); return state(f); }).then(function (s) { assert.strictEqual(s.hash, sha(before)); assert.strictEqual(s.fenced, true); });
}); });
check('activation requires live arming and an unexpired request', function () { return fixture(function (f) {
    ok(f, 'prepare', 'prepared'); refused(invoke(f, 'activate')); original(f);
    refused(invoke(f, 'arm', function (r) { r.expiresAtUnixMs = Date.now()-1; }));
}); });
check('lost prepare acknowledgement cannot be replayed', function () { return fixture(function (f) { ok(f, 'prepare', 'prepared'); refused(invoke(f, 'prepare')); original(f); }); });
check('wrong identity binding refuses before creating agent state', function () { return fixture(function (f) {
    refused(invoke(f, 'prepare', function (r) { r.binding = JSON.parse(JSON.stringify(r.binding)); r.binding.identity.credentialVersion = 2; }));
    assert.strictEqual(fs.existsSync(path.join(f.helper, 'reserved.json')), false); original(f);
}); });
check('source tampering refuses before arming or credential writes', function () { return fixture(function (f) {
    fs.appendFileSync(path.join(f.helper, 'transaction.js'), '\nchanged\n'); refused(invoke(f, 'prepare')); original(f);
}); });
check('symlinked source and unsafe private helper mode refuse', function () { return fixture(function (f) {
    var p = path.join(f.helper, 'consumer-profile.js'); fs.renameSync(p, p+'.original'); fs.symlinkSync(p+'.original', p); refused(invoke(f, 'prepare')); fs.unlinkSync(p); fs.renameSync(p+'.original', p);
    fs.chmodSync(f.helper, 493); refused(invoke(f, 'prepare')); fs.chmodSync(f.helper, 448); original(f);
    var target = path.join(f.root, 'credentials.json'); fs.chmodSync(target, f.stock ? 384 : 438); refused(invoke(f, 'inspect')); fs.chmodSync(target, f.stock ? 438 : 384);
    if (!f.stock) { f.config.transactionProfile = 'stock-root-0666-to-0600'; write(path.join(f.helper, 'agent.json'), f.config); fs.chmodSync(target, 438); refused(invoke(f, 'inspect')); f.config.transactionProfile = 'private-0600'; write(path.join(f.helper, 'agent.json'), f.config); fs.chmodSync(target, 384); }
    fs.writeFileSync(target, Buffer.from('unexpected baseline')); refused(invoke(f, 'inspect')); assert.strictEqual(fs.readFileSync(target).toString(), 'unexpected baseline');
}); });
check('unrelated credential edits survive failed watchdog recovery', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); var bytes = Buffer.from('unrelated change'); fs.writeFileSync(path.join(f.root, 'credentials.json'), bytes);
    return poll(function () { return fs.existsSync(path.join(f.helper, 'recovery-unverified.json')); }).then(function () { assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(bytes)); assert.ok(fs.existsSync(path.join(f.root, '.credential-recovery-owner'))); });
}); });
check('failed new reload triggers original restoration', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); refused(invoke(f, 'reload'));
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); }).then(function () { original(f); return state(f); }).then(function (s) { assert.strictEqual(s.hash, sha(before)); });
}, { failNew: true }); });
check('failed original reload repairs file but cannot claim consumer recovery', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); ok(f, 'reload', 'reload-started'); refused(invoke(f, 'restore'));
    original(f); assert.ok(fs.existsSync(path.join(f.helper, 'recovery-unverified.json'))); assert.strictEqual(fs.existsSync(path.join(f.helper, 'restored.json')), false);
}, { failOriginal: true, watchdogMs: 5000 }); });
check('unverified downstream fencing prevents restoration from racing a reload', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); refused(invoke(f, 'restore')); assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(after));
    assert.strictEqual(fs.existsSync(path.join(f.helper, 'restored.json')), false); refused(invoke(f, 'reload'));
}, { failFence: true }); });
check('timed-out reload cannot apply after the downstream fence', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); refused(invoke(f, 'reload'));
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); }).then(function () { return delay(1000); }).then(function () { original(f); return state(f); }).then(function (s) { assert.strictEqual(s.newReloads, 0); assert.strictEqual(s.hash, sha(before)); assert.strictEqual(s.pending, 0); });
}, { delayNew: 1500, actionTimeoutMs: 250, watchdogMs: 5000 }); });
check('watchdog also closes the gap between preparation and arming', function () { return fixture(function (f) {
    ok(f, 'prepare', 'prepared');
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); }).then(function () { original(f); refused(invoke(f, 'activate')); });
}); });
check('stale transaction lock is retained and recovery remains unverified', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); var lock = path.join(f.root, '.credential-delivery-lock'); fs.mkdirSync(lock, 448);
    refused(invoke(f, 'restore')); assert.ok(fs.lstatSync(lock).isDirectory()); assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(after));
    assert.ok(fs.existsSync(path.join(f.helper, 'recovery-unverified.json')));
}); });
check('forward replay stays fenced after successful file restoration', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); ok(f, 'restore', 'restored'); refused(invoke(f, 'arm')); refused(invoke(f, 'reload')); original(f);
}, { watchdogMs: 5000 }); });
check('unknown request keys and duplicate JSON names cannot conceal a phase', function () { return fixture(function (f) {
    refused(invoke(f, 'prepare', function (r) { r.extra = true; }));
    var r = JSON.stringify(request(f, 'prepare')); r = r.replace('"phase":"prepare"', '"phase":"activate","phase":"prepare"');
    refused(cp.spawnSync(process.execPath, [path.join(f.helper, 'driver.js')], { input: r+'\n', encoding: 'utf8', timeout: 4000 })); original(f);
}); });
check('phase replies and errors disclose no credential bytes', function () { return fixture(function (f) {
    var text = JSON.stringify(ok(f, 'inspect', 'qualified')); ['old-fixture-secret','new-fixture-secret','secretAccessKey'].forEach(function (s) { assert.strictEqual(text.indexOf(s), -1); });
    var r = invoke(f, 'prepare', function (v) { v.replacement = Buffer.from('sensitive invalid input').toString('base64'); }); refused(r); assert.strictEqual(r.stderr.indexOf('sensitive'), -1);
}); });
check('killing an in-flight RPC controller does not kill the watchdog', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed');
    var child = cp.spawn(process.execPath, [path.join(f.helper, 'driver.js')], { stdio: ['pipe','ignore','ignore'] }); child.stdin.end(JSON.stringify(request(f, 'reload'))+'\n');
    return poll(function () { return fs.existsSync(path.join(f.helper, 'reload-requested.json')); }).then(function () { child.kill('SIGKILL'); return poll(function () { return fs.existsSync(path.join(f.helper, 'closed.json')); }); }).then(function () {
        original(f); return state(f);
    }).then(function (s) { assert.strictEqual(s.hash, sha(before)); assert.strictEqual(s.pending, 0); });
}, { delayNew: 400, watchdogMs: 2500 }); });
check('watchdog death refuses future admission and makes no false recovery claim', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed');
    var pid = JSON.parse(fs.readFileSync(path.join(f.helper, 'daemon-started.json'))).pid;
    assert.ok(fs.readFileSync('/proc/'+pid+'/cmdline').toString().indexOf(path.join(f.helper, 'driver.js')+'\u0000--daemon') >= 0); process.kill(pid, 'SIGKILL');
    return delay(100).then(function () { refused(invoke(f, 'reload')); assert.strictEqual(fs.existsSync(path.join(f.helper, 'restored.json')), false); assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(after)); assert.ok(fs.existsSync(path.join(f.root, '.credential-recovery-owner'))); });
}); });
check('configuration drift refuses forward work and preserves its evidence', function () { return fixture(function (f) {
    prepare(f); f.config.actionTimeoutMs = 600; write(path.join(f.helper, 'agent.json'), f.config); refused(invoke(f, 'activate')); original(f);
}); });
check('partial quiesce failure still returns the consumer to its original state', function () { return fixture(function (f) {
    prepare(f); refused(invoke(f, 'activate'));
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); }).then(function () { original(f); return state(f); }).then(function (s) { assert.strictEqual(s.paused, false); assert.strictEqual(s.hash, sha(before)); });
}, { failQuiesce: true }); });
check('expiry while quiescing denies the file replacement', function () { return fixture(function (f) {
    prepare(f); refused(invoke(f, 'activate', function (r) { r.expiresAtUnixMs = Date.now()+150; })); original(f);
    return poll(function () { return fs.existsSync(path.join(f.helper, 'restored.json')); });
}, { delayQuiesce: 300 }); });
check('independent original observation is required after file repair', function () { return fixture(function (f) {
    prepare(f); ok(f, 'activate', 'installed'); write(path.join(f.root, 'fixture-options.json'), { failObserve: true }); refused(invoke(f, 'restore')); original(f);
    assert.strictEqual(fs.existsSync(path.join(f.helper, 'restored.json')), false); assert.ok(fs.existsSync(path.join(f.helper, 'recovery-unverified.json')));
}); });
check('unit serialization and verified closeout permit a fresh operation only', function () { return fixture(function (f) {
    prepare(f);
    var second = Object.assign({}, f), id = crypto.randomBytes(16).toString('hex'); second.binding = Object.assign({}, f.binding, { operationId: id }); second.helper = path.join(f.root, '.openjibo-delivery-'+id); fs.mkdirSync(second.helper, 448);
    ['driver.js','transaction.js','consumer-profile.js'].forEach(function (name) { write(path.join(second.helper, name), fs.readFileSync(path.join(f.helper, name))); });
    write(path.join(second.helper, 'agent.json'), Object.assign({}, f.config, { binding: second.binding }));
    refused(invoke(second, 'prepare')); assert.strictEqual(fs.existsSync(path.join(second.helper, 'reserved.json')), false);
    ok(f, 'restore', 'restored');
    return poll(function () { return fs.existsSync(path.join(f.helper, 'closed.json')); }).then(function () {
        prepare(second); ok(second, 'activate', 'installed'); refused(invoke(f, 'restore')); assert.ok(fs.readFileSync(path.join(f.root, 'credentials.json')).equals(after)); ok(second, 'restore', 'restored'); original(f);
    });
}, { watchdogMs: 2200 }); });
var passed = 0, sequence = Promise.resolve(); groups.forEach(function (g) { sequence = sequence.then(g.run).then(function () { passed++; }); });
sequence.then(function () { console.log(JSON.stringify({ groupsPassed: passed, posixAcceptance: true, rootStockProfileAccepted: process.getuid() === 0, detachedWatchdogTested: true, residentConsumerTested: true, robotContacted: false, realCredentialsRead: false, physicalReloadQualified: false })); }, function (e) { console.error(e.stack); process.exitCode = 1; });
