'use strict';
var assert = require('assert'), fs = require('fs'), os = require('os'), path = require('path');
var cp = require('child_process'), tool = require('./credential-delivery-transaction.js');
var count = 0, faultCases = 0;
function check(name, run) { run(); count++; }
function refuse(run) { assert.throws(run, /^Error: Credential delivery refused\.$/); }
var before = Buffer.from('{\n "accessKeyId":"fixture-old", "secretAccessKey":"old-secret-0123456789", "region":"api"\n}\n');
var after = Buffer.from('{"accessKeyId":"fixture-new","secretAccessKey":"new-secret-9876543210","region":"api"}\n');
function plan() { return { schema: 'openjibo.credential-delivery.v1', operationId: 'a'.repeat(32),
    beforeSha256: tool.sha256(before), afterSha256: tool.sha256(after),
    mappingEvidenceSha256: 'b'.repeat(64), recoveryEvidenceSha256: 'c'.repeat(64) }; }
function remove(dir) {
    fs.readdirSync(dir).forEach(function (name) {
        var full = path.join(dir, name);
        if (fs.lstatSync(full).isDirectory()) { remove(full); fs.rmdirSync(full); } else fs.unlinkSync(full);
    });
}
function fixture(run) {
    var dir = fs.mkdtempSync(path.join(os.tmpdir(), 'credential-delivery-test-'));
    fs.chmodSync(dir, 448); fs.writeFileSync(path.join(dir, 'credentials.json'), before, { mode: 384 });
    try { run(dir, tool.create(dir)); }
    finally { remove(dir); fs.rmdirSync(dir); }
}
function journal(dir, name) { return path.join(dir, '.credential-delivery-' + 'a'.repeat(32), name); }
check('strict flat credentials parse; no duplicate or escaped alias overwrite', function () {
    assert.strictEqual(tool.validateCredential(before).region, 'api');
    ['{}', '{"accessKeyId":"x"}', '{"accessKeyId":"a","accessKeyId":"b","secretAccessKey":"0123456789012345"}',
        '{"accessKeyId":"a","accessKey\\u0049d":"b","secretAccessKey":"0123456789012345"}',
        '{"accessKeyId":"a","secretAccessKey":{},"region":"api"}',
        '{"accessKeyId":"a","secretAccessKey":"short"}',
        '{"accessKeyId":"a/b","secretAccessKey":"0123456789012345"}',
        '{"accessKeyId":"a","secretAccessKey":"0123456789012345","extra":"x"}',
        before.toString() + 'true', before.toString().replace('"api"', '"unknown"'),
        before.toString().replace('fixture-old', 'bad id'), before.toString().replace('fixture-old', '\\ud800')]
        .forEach(function (value) { refuse(function () { tool.validateCredential(Buffer.from(value)); }); });
    refuse(function () { tool.validateCredential(Buffer.from([255])); });
    refuse(function () { tool.validateCredential(Buffer.alloc(16385, 32)); });
});
check('standalone execution refuses without touching a credential path', function () {
    var run = cp.spawnSync(process.execPath, [path.join(__dirname, 'credential-delivery-transaction.js'), '/var/jibo'], { encoding: 'utf8' });
    assert.strictEqual(run.status, 2); assert.strictEqual(run.stdout, '');
    assert.strictEqual(run.stderr, 'Credential delivery has no standalone execution interface.\n');
});
if (process.platform === 'win32') {
    refuse(function () { tool.create(process.cwd()); });
    console.log(JSON.stringify({ cases: count + 1, posixAcceptance: false, windowsMutationRefused: true }));
    process.exit(0);
}
check('prepare preserves bytes, activation is explicit, rollback is exact and terminal', function () { fixture(function (dir, tx) {
    var p = plan(), target = path.join(dir, 'credentials.json');
    assert.strictEqual(tx.prepare(p, after).state, 'staged'); assert.ok(fs.readFileSync(target).equals(before));
    assert.strictEqual(tx.prepare(p, after).state, 'staged');
    var result = tx.activate(p); assert.strictEqual(result.state, 'installed'); assert.strictEqual(result.lockPresent, false);
    assert.ok(fs.readFileSync(target).equals(after)); assert.strictEqual(tx.activate(p).state, 'installed');
    assert.strictEqual(tx.restore(p).state, 'restored'); assert.ok(fs.readFileSync(target).equals(before));
    assert.strictEqual(tx.restore(p).state, 'restored'); refuse(function () { tx.activate(p); });
    [target, journal(dir, 'before.bin'), journal(dir, 'after.bin'), journal(dir, 'plan.json')].forEach(function (file) {
        assert.strictEqual(fs.statSync(file).mode & 511, 384);
    });
    var output = JSON.stringify(result);
    ['old-secret', 'new-secret', 'fixture-new', p.beforeSha256, p.mappingEvidenceSha256].forEach(function (s) { assert.strictEqual(output.indexOf(s), -1); });
    ['ownershipVerified', 'recoveryVerified', 'robotContactPerformed', 'serviceAccessGranted'].forEach(function (key) { assert.strictEqual(result[key], false); });
}); });
check('abort staged transaction never installs credentials', function () { fixture(function (dir, tx) {
    tx.prepare(plan(), after); assert.strictEqual(tx.restore(plan()).state, 'restored');
    assert.ok(fs.readFileSync(path.join(dir, 'credentials.json')).equals(before));
}); });
check('plan context, before/after and operation IDs cannot be substituted', function () { fixture(function (dir, tx) {
    var p = plan(); p.beforeSha256 = '0'.repeat(64); refuse(function () { tx.prepare(p, after); });
    p = plan(); p.afterSha256 = '0'.repeat(64); refuse(function () { tx.prepare(p, after); });
    p = plan(); p.operationId = '../elsewhere'; refuse(function () { tx.prepare(p, after); });
    p = plan(); p.extra = true; refuse(function () { tx.prepare(p, after); });
    tx.prepare(plan(), after); p = plan(); p.mappingEvidenceSha256 = 'd'.repeat(64);
    refuse(function () { tx.activate(p); }); p = plan(); p.recoveryEvidenceSha256 = 'd'.repeat(64);
    refuse(function () { tx.restore(p); });
}); });
check('routing changes and cosmetic-only replacements refuse', function () { fixture(function (dir, tx) {
    var changed = Buffer.from(after.toString().replace('"api"', '"open-jibo"')), p = plan();
    p.afterSha256 = tool.sha256(changed); refuse(function () { tx.prepare(p, changed); });
    changed = Buffer.from(JSON.stringify(JSON.parse(before.toString()))); p.afterSha256 = tool.sha256(changed);
    refuse(function () { tx.prepare(p, changed); });
}); });
check('unknown edits are never overwritten during activation or restoration', function () {
    ['activate', 'restore'].forEach(function (action) { fixture(function (dir, tx) {
        tx.prepare(plan(), after); if (action === 'restore') tx.activate(plan());
        var edit = Buffer.from('unrelated edit'), target = path.join(dir, 'credentials.json'); fs.writeFileSync(target, edit);
        assert.strictEqual(tx.inspect(plan()).state, 'diverged'); refuse(function () { tx[action](plan()); });
        assert.ok(fs.readFileSync(target).equals(edit));
    }); });
});
check('corrupt original, replacement and manifest each refuse restoration', function () {
    ['before.bin', 'after.bin', 'plan.json'].forEach(function (name) { fixture(function (dir, tx) {
        tx.prepare(plan(), after); tx.activate(plan()); fs.writeFileSync(journal(dir, name), 'corrupt');
        refuse(function () { tx.restore(plan()); }); assert.ok(fs.readFileSync(path.join(dir, 'credentials.json')).equals(after));
    }); });
});
check('credential symlinks, hard links and permissive modes refuse', function () {
    ['symlink', 'hardlink', 'mode'].forEach(function (kind) { fixture(function (dir, tx) {
        var target = path.join(dir, 'credentials.json'), other = path.join(dir, 'other');
        if (kind === 'symlink') { fs.renameSync(target, other); fs.symlinkSync(other, target); }
        if (kind === 'hardlink') fs.linkSync(target, other);
        if (kind === 'mode') fs.chmodSync(target, 420);
        refuse(function () { tx.prepare(plan(), after); });
    }); });
});
check('symlink root and writable-by-others root refuse', function () { fixture(function (dir) {
    refuse(function () { tool.create(path.join(dir, 'missing-private-path')); });
    fs.symlinkSync(dir, path.join(dir, 'alias')); refuse(function () { tool.create(path.join(dir, 'alias')); });
    fs.chmodSync(dir, 511); refuse(function () { tool.create(dir); }); fs.chmodSync(dir, 448);
}); });
check('partial writes complete; zero-progress and partial backup failures preserve target', function () {
    ['short', 'zero', 'throw'].forEach(function (kind) { fixture(function (dir) {
        var io = Object.create(fs), calls = 0;
        io.writeSync = function (fd, bytes, offset, length, position) {
            calls++;
            if (kind === 'zero') return 0;
            if (kind === 'throw' && calls > 1) throw new Error('private path or provider details');
            return fs.writeSync(fd, bytes, offset, Math.min(length, 7), position);
        };
        var tx = tool.create(dir, io);
        if (kind === 'short') {
            tx.prepare(plan(), after); tx.activate(plan()); tx.restore(plan());
        } else refuse(function () { tx.prepare(plan(), after); });
        assert.ok(fs.readFileSync(path.join(dir, 'credentials.json')).equals(before));
    }); });
});
check('stale lock is visible; never automatically stolen', function () { fixture(function (dir, tx) {
    tx.prepare(plan(), after); fs.mkdirSync(path.join(dir, '.credential-delivery-lock'), 448);
    assert.strictEqual(tx.inspect(plan()).lockPresent, true);
    refuse(function () { tx.activate(plan()); }); refuse(function () { tx.restore(plan()); });
    assert.ok(fs.statSync(path.join(dir, '.credential-delivery-lock')).isDirectory());
}); });
check('process death after rename preserves original and refuses stale-lock replay', function () { fixture(function (dir, tx) {
    tx.prepare(plan(), after);
    var driver = "var fs=require('fs'),tool=require(" + JSON.stringify(path.join(__dirname, 'credential-delivery-transaction.js')) + ");" +
        "var io=Object.create(fs);io.renameSync=function(a,b){fs.renameSync(a,b);process.exit(77);};" +
        'tool.create(' + JSON.stringify(dir) + ',io).activate(' + JSON.stringify(plan()) + ');';
    var result = cp.spawnSync(process.execPath, ['-e', driver], { encoding: 'utf8', timeout: 5000 });
    assert.strictEqual(result.status, 77); assert.strictEqual(result.stdout, ''); assert.strictEqual(result.stderr, '');
    assert.strictEqual(tx.inspect(plan()).state, 'installed'); assert.strictEqual(tx.inspect(plan()).lockPresent, true);
    assert.ok(fs.readFileSync(journal(dir, 'before.bin')).equals(before)); refuse(function () { tx.restore(plan()); });
    // Fixture operator has observed the child exit. No library lock-breaking API exists.
    fs.rmdirSync(path.join(dir, '.credential-delivery-lock'));
    assert.strictEqual(tool.create(dir).restore(plan()).state, 'restored');
}); });
// Inject before/after every mutating filesystem call in the successful lifecycle.
// Real files exercise atomic replacement; exceptions are not a power-loss model.
var names = ['mkdirSync', 'openSync', 'writeSync', 'fsyncSync', 'renameSync', 'rmdirSync'];
function wrapped(at, afterCall, trace) {
    var io = Object.create(fs), index = 0;
    names.forEach(function (name) { io[name] = function () {
        var mutation = name !== 'openSync' || arguments[1] === 'wx';
        var hit = mutation && ++index === at;
        if (mutation) trace.push(name);
        if (hit && !afterCall) throw new Error('injected storage failure');
        var result = fs[name].apply(fs, arguments);
        if (hit && afterCall) throw new Error('injected storage failure');
        return result;
    }; }); return io;
}
var trace = [];
fixture(function (dir) { var tx = tool.create(dir, wrapped(-1, false, trace)); tx.prepare(plan(), after); tx.activate(plan()); tx.restore(plan()); });
[false, true].forEach(function (afterCall) { trace.forEach(function (_, index) { fixture(function (dir) {
    var tx = tool.create(dir, wrapped(index + 1, afterCall, []));
    try { tx.prepare(plan(), after); tx.activate(plan()); tx.restore(plan()); } catch (e) { assert.strictEqual(e.message, 'Credential delivery refused.'); }
    var current = fs.readFileSync(path.join(dir, 'credentials.json'));
    assert.ok(current.equals(before) || current.equals(after), 'no torn credential target');
    if (current.equals(after)) {
        assert.ok(fs.readFileSync(journal(dir, 'before.bin')).equals(before));
        assert.ok(fs.readFileSync(journal(dir, 'after.bin')).equals(after));
        assert.strictEqual(JSON.parse(fs.readFileSync(journal(dir, 'plan.json'), 'utf8')).recoveryEvidenceSha256, plan().recoveryEvidenceSha256);
    }
    faultCases++;
}); }); });
console.log(JSON.stringify({ cases: count, filesystemFaultCases: faultCases, posixAcceptance: true,
    robotContactPerformed: false, physicalRecoveryVerified: false }));
