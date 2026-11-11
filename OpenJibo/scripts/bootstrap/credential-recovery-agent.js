/* ES5 / Node 6.9.2. Explicit reviewed local profile only; no installer. */
'use strict';
var fs = require('fs'), path = require('path'), crypto = require('crypto');
var cp = require('child_process'), net = require('net');
var REFUSAL = 'Credential recovery agent refused.';
function need(ok) { if (!ok) throw new Error(REFUSAL); }
function sha(b) { return crypto.createHash('sha256').update(b).digest('hex'); }
function exact(v, keys) { need(v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).sort().join(' ') === keys.split(' ').sort().join(' ')); }
function sync(dir) { var fd = fs.openSync(dir, 'r'); try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); } }
function read(file, limit, modes) {
    modes = modes || [384];
    var st = fs.lstatSync(file); need(st.isFile() && st.nlink === 1 && st.uid === process.getuid() && st.gid === process.getgid() && modes.indexOf(st.mode & 4095) >= 0 && st.size > 0 && st.size <= limit);
    var fd = fs.openSync(file, fs.constants.O_RDONLY | fs.constants.O_NOFOLLOW);
    try {
        var opened = fs.fstatSync(fd); need(opened.ino === st.ino && opened.dev === st.dev && opened.size === st.size && opened.mode === st.mode && opened.uid === st.uid && opened.gid === st.gid && opened.nlink === 1);
        var data = Buffer.alloc(limit + 1), at = 0, n;
        while (at <= limit && (n = fs.readSync(fd, data, at, data.length - at, null))) at += n;
        var end = fs.fstatSync(fd); need(at === st.size && end.size === st.size && end.mtime.getTime() === st.mtime.getTime() && end.ctime.getTime() === st.ctime.getTime());
        return data.slice(0, at);
    } finally { fs.closeSync(fd); }
}
function parse(raw) {
    var text = raw.toString('utf8'); need(Buffer.from(text).equals(raw));
    var value = JSON.parse(text); need(text === JSON.stringify(value) + '\n'); return value;
}
function write(file, value) {
    var bytes = Buffer.from(JSON.stringify(value) + '\n'), fd = fs.openSync(file, 'wx', 384);
    try { var at = 0; while (at < bytes.length) { var n = fs.writeSync(fd, bytes, at, bytes.length-at, null); need(n > 0); at += n; } fs.fsyncSync(fd); }
    finally { fs.closeSync(fd); } sync(path.dirname(file));
}
function exists(file) { try { fs.lstatSync(file); return true; } catch (e) { if (e.code === 'ENOENT') return false; throw e; } }
function binding(b) {
    exact(b, 'operationId hostKeySha256 beforeSha256 afterSha256 recoverySha256 identity associationSha256 consumerProfileSha256 driverSha256 transactionSha256 scenario');
    need(typeof b.operationId === 'string' && /^[a-f0-9]{32}$/.test(b.operationId));
    ['beforeSha256','afterSha256','recoverySha256','associationSha256','consumerProfileSha256','driverSha256','transactionSha256'].forEach(function (key) { need(typeof b[key] === 'string' && /^[a-f0-9]{64}$/.test(b[key])); });
    need(b.beforeSha256 !== b.afterSha256 && typeof b.hostKeySha256 === 'string' && /^SHA256:[A-Za-z0-9+/]{43}$/.test(b.hostKeySha256) && (b.scenario === 0 || b.scenario === 1));
    exact(b.identity, 'robotId assignmentId pairingId deviceKeyId credentialVersion');
    ['robotId','assignmentId','pairingId','deviceKeyId'].forEach(function (key) { need(typeof b.identity[key] === 'string' && /^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(b.identity[key]) && b.identity[key] !== '00000000-0000-0000-0000-000000000000'); });
    need(typeof b.identity.credentialVersion === 'number' && b.identity.credentialVersion % 1 === 0 && b.identity.credentialVersion > 0);
}
function context() {
    need(process.platform !== 'win32' && typeof process.getuid === 'function');
    var helper = path.dirname(__filename), c = parse(read(path.join(helper, 'agent.json'), 8192));
    exact(c, 'schema binding credentialDirectory transactionProfile watchdogMs actionTimeoutMs');
    need(c.schema === 'openjibo.credential-agent-config.v1'); binding(c.binding);
    need(path.basename(helper) === '.openjibo-delivery-' + c.binding.operationId && fs.realpathSync(helper) === helper);
    var st = fs.lstatSync(helper); need(st.isDirectory() && st.uid === process.getuid() && st.gid === process.getgid() && (st.mode & 4095) === 448);
    need(typeof c.credentialDirectory === 'string' && path.isAbsolute(c.credentialDirectory) && path.resolve(c.credentialDirectory) === c.credentialDirectory && c.credentialDirectory !== '/' && path.dirname(helper) === c.credentialDirectory);
    var parent = fs.lstatSync(c.credentialDirectory); need(fs.realpathSync(c.credentialDirectory) === c.credentialDirectory && parent.isDirectory() && parent.uid === process.getuid() && parent.gid === process.getgid() && (parent.mode & 18) === 0);
    need(c.transactionProfile === 'private-0600' || c.transactionProfile === 'stock-root-0666-to-0600');
    need(typeof c.watchdogMs === 'number' && c.watchdogMs % 1 === 0 && c.watchdogMs >= 500 && c.watchdogMs <= 30000);
    need(typeof c.actionTimeoutMs === 'number' && c.actionTimeoutMs % 1 === 0 && c.actionTimeoutMs >= 100 && c.actionTimeoutMs <= 1000);
    need(sha(read(__filename, 65536)) === c.binding.driverSha256 && sha(read(path.join(helper, 'transaction.js'), 65536)) === c.binding.transactionSha256 && sha(read(path.join(helper, 'consumer-profile.js'), 65536)) === c.binding.consumerProfileSha256);
    var plan = { schema: c.transactionProfile === 'private-0600' ? 'openjibo.credential-delivery.v1' : 'openjibo.credential-delivery.v2', operationId: c.binding.operationId,
        beforeSha256: c.binding.beforeSha256, afterSha256: c.binding.afterSha256, mappingEvidenceSha256: c.binding.associationSha256, recoveryEvidenceSha256: c.binding.recoverySha256 };
    if (plan.schema.slice(-2) === 'v2') plan.filePermissionProfile = c.transactionProfile;
    return { c: c, helper: helper, socket: path.join(helper, 'agent.sock'), claim: path.join(c.credentialDirectory, '.credential-recovery-owner'), plan: plan,
        tx: require(path.join(helper, 'transaction.js')).create(c.credentialDirectory) };
}
function event(s, name) {
    var file = path.join(s.helper, name + '.json'), value = { phase: name, bindingSha256: sha(Buffer.from(JSON.stringify(s.c.binding))) };
    if (name === 'daemon-started') value.pid = process.pid;
    if (exists(file)) need(JSON.stringify(parse(read(file, 4096))) === JSON.stringify(value)); else write(file, value);
}
function has(s, name) { return exists(path.join(s.helper, name + '.json')); }
function verify(s) { need(JSON.stringify(context().c) === JSON.stringify(s.c)); }
function claim(s, fresh) {
    if (fresh) { fs.mkdirSync(s.claim, 448); sync(s.c.credentialDirectory); write(path.join(s.claim, 'owner.json'), { operationId: s.c.binding.operationId }); }
    var st = fs.lstatSync(s.claim); need(st.isDirectory() && st.uid === process.getuid() && st.gid === process.getgid() && (st.mode & 4095) === 448);
    need(JSON.stringify(parse(read(path.join(s.claim, 'owner.json'), 4096))) === JSON.stringify({ operationId: s.c.binding.operationId }));
}
function request(s, r) {
    exact(r, 'schema phase binding replacement requestNonce expiresAtUnixMs');
    need(r.schema === 'openjibo.owned-delivery-rpc.v1' && JSON.stringify(r.binding) === JSON.stringify(s.c.binding));
    need(['inspect','prepare','arm','activate','reload','fence','restore'].indexOf(r.phase) >= 0);
    need(typeof r.requestNonce === 'string' && /^[a-f0-9]{32}$/.test(r.requestNonce));
    need(typeof r.expiresAtUnixMs === 'number' && r.expiresAtUnixMs % 1 === 0 && r.expiresAtUnixMs > Date.now() && r.expiresAtUnixMs <= Date.now() + 5000);
    if (r.phase === 'prepare') {
        need(typeof r.replacement === 'string' && r.replacement.length <= 21848);
        var replacement = Buffer.from(r.replacement, 'base64'); need(replacement.toString('base64') === r.replacement && replacement.length <= 16384 && sha(replacement) === s.c.binding.afterSha256);
    } else need(r.replacement === null);
}
function receipt(s, r, state) { return { schema: 'openjibo.owned-delivery-receipt.v1', phase: r.phase, state: state, binding: s.c.binding, requestNonce: r.requestNonce }; }
function framed(stream, max, done) {
    var parts = [], length = 0, completed = false;
    function finish(e, value) { if (!completed) { completed = true; done(e, value); } }
    stream.on('data', function (b) { length += b.length; if (length > max) { finish(new Error(REFUSAL)); stream.destroy(); } else parts.push(b); });
    stream.on('end', function () { try { if (!completed) finish(null, parse(Buffer.concat(parts))); } catch (e) { finish(e); } });
    stream.on('error', function (e) { finish(e); });
}
function profile(s, action, admission, running) {
    // The profile must itself fence downstream work; process death alone does
    // not settle a consumer RPC already admitted by another process.
    return new Promise(function (resolve, reject) {
        verify(s);
        var child = cp.spawn(process.execPath, [__filename, '--consumer', action], { stdio: ['pipe','pipe','pipe'], env: {} });
        running.child = child; var result, failed = false, errorBytes = 0;
        var timer = setTimeout(function () { failed = true; child.kill('SIGKILL'); }, s.c.actionTimeoutMs);
        child.on('error', function () { failed = true; });
        child.stdout.on('error', function () { failed = true; });
        child.stderr.on('data', function (b) { errorBytes += b.length; if (errorBytes > 4096) child.kill('SIGKILL'); });
        framed(child.stdout, 4096, function (e, value) { if (e) failed = true; else result = value; });
        child.stdin.on('error', function () { failed = true; });
        child.stdin.end(JSON.stringify({ action: action, binding: s.c.binding, expiresAtUnixMs: admission }) + '\n');
        child.on('close', function (code) {
            clearTimeout(timer); if (running.child === child) running.child = null;
            if (failed || errorBytes || code !== 0 || !result) return reject(new Error(REFUSAL));
            try { exact(result, 'action operationId verified'); need(result.action === action && result.operationId === s.c.binding.operationId && result.verified === true); resolve(); } catch (e) { reject(e); }
        });
    });
}
function consumer(action) {
    var s = context(); need(['inspect','quiesce','reloadNew','fence','reloadOriginal','observeNew','observeOriginal'].indexOf(action) >= 0);
    framed(process.stdin, 8192, function (error, value) {
        if (error) return fail();
        try {
            exact(value, 'action binding expiresAtUnixMs'); need(value.action === action && JSON.stringify(value.binding) === JSON.stringify(s.c.binding));
            need(typeof value.expiresAtUnixMs === 'number' && value.expiresAtUnixMs > Date.now() && value.expiresAtUnixMs <= Date.now() + 5000);
            var hook = require(path.join(s.helper, 'consumer-profile.js')); need(typeof hook.run === 'function');
            Promise.resolve(hook.run(action, value, s.c.credentialDirectory)).then(function (verified) { need(verified === true); process.stdout.write(JSON.stringify({ action: action, operationId: s.c.binding.operationId, verified: true }) + '\n'); }).catch(fail);
        } catch (_) { fail(); }
    });
}
function daemon() {
    var s = context(); claim(s, false); need(has(s, 'reserved') && !has(s, 'daemon-started') && s.socket.length < 104);
    event(s, 'daemon-started'); var started = process.hrtime(), stopping = false, armed = false, running = {}, tail = Promise.resolve(), closed = false;
    function elapsed() { var t = process.hrtime(started); return t[0]*1000 + t[1]/1000000; }
    function terminal() { return stopping || has(s, 'fenced') || elapsed() >= s.c.watchdogMs; }
    function fence() {
        stopping = true; event(s, 'fenced');
        return profile(s, 'fence', Date.now()+2000, running).then(function () { event(s, 'consumer-fenced'); });
    }
    function restore() {
        return fence().then(function () {
            need(s.tx.restore(s.plan).state === 'restored');
            return has(s, 'restored') ? Promise.resolve() : profile(s, 'reloadOriginal', Date.now()+2000, running);
        }).then(function () { return profile(s, 'observeOriginal', Date.now()+2000, running); }).then(function () {
            need(s.tx.inspect(s.plan).state === 'restored'); event(s, 'restored');
            // Keep the unit claim until the supervisor's verified closeout.
        }).catch(function () { event(s, 'recovery-unverified'); throw new Error(REFUSAL); });
    }
    function forward(r) { verify(s); request(s, r); need(!terminal()); claim(s, false); }
    function run(r) {
        verify(s); request(s, r); claim(s, false);
        if (r.phase === 'fence') return fence().then(function () { return 'forward-fenced'; });
        if (r.phase === 'restore') return restore().then(function () { return 'restored'; });
        forward(r);
        if (r.phase === 'prepare') {
            need(!has(s, 'prepare-requested')); event(s, 'prepare-requested');
            need(s.tx.prepare(s.plan, Buffer.from(r.replacement, 'base64')).state === 'staged'); event(s, 'prepared'); return Promise.resolve('prepared');
        }
        need(has(s, 'prepared'));
        if (r.phase === 'arm') { need(!has(s, 'armed') && elapsed() < s.c.watchdogMs-100); armed = true; event(s, 'armed'); return Promise.resolve('recovery-armed'); }
        need(armed && has(s, 'armed'));
        if (r.phase === 'activate') {
            need(!has(s, 'activate-requested')); event(s, 'activate-requested');
            return profile(s, 'quiesce', r.expiresAtUnixMs, running).then(function () {
                forward(r); need(s.tx.activate(s.plan).state === 'installed'); event(s, 'installed'); return 'installed';
            });
        }
        need(r.phase === 'reload' && s.c.binding.scenario === 1 && has(s, 'installed') && !has(s, 'reload-requested'));
        event(s, 'reload-requested');
        return profile(s, 'reloadNew', r.expiresAtUnixMs, running).then(function () { forward(r); event(s, 'reload-started'); return 'reload-started'; });
    }
    function stopSoon() { stopping = true; if (running.child) running.child.kill('SIGKILL'); }
    function closeout() {
        verify(s); claim(s, false); need(has(s, 'restored') && s.tx.inspect(s.plan).state === 'restored');
        return profile(s, 'observeOriginal', Date.now()+2000, running).then(function () {
            claim(s, false); need(s.tx.inspect(s.plan).state === 'restored' && fs.readdirSync(s.claim).sort().join(' ') === 'owner.json');
            event(s, 'closeout-verified'); fs.unlinkSync(path.join(s.claim, 'owner.json')); fs.rmdirSync(s.claim); sync(s.c.credentialDirectory); event(s, 'closed');
        });
    }
    function queue(fn) { var next = tail.then(fn); tail = next.catch(function () {}); return next; }
    var server = net.createServer({ allowHalfOpen: true }, function (socket) {
        socket.setTimeout(2500, function () { socket.destroy(); }); socket.on('error', function () {});
        framed(socket, 65536, function (error, r) {
            if (error) return socket.end('refused\n');
            try { request(s, r); if (r.phase === 'fence' || r.phase === 'restore') stopSoon(); }
            catch (_) { return socket.end('refused\n'); }
            queue(function () { return run(r); }).then(function (state) { socket.end(JSON.stringify(receipt(s, r, state))+'\n'); }, function () {
                socket.end('refused\n');
                if (!stopping && has(s, 'prepare-requested')) { stopSoon(); queue(restore).catch(function () {}); }
            });
        });
    });
    server.on('error', function () { stopSoon(); queue(restore).catch(function () {}); });
    server.listen(s.socket, function () { fs.chmodSync(s.socket, 384); event(s, 'ready'); });
    var timer = setInterval(function () {
        if (elapsed() >= s.c.watchdogMs && !closed) {
            closed = true; stopSoon();
            queue(function () { return has(s, 'prepare-requested') && !has(s, 'restored') ? restore() : Promise.resolve(); }).then(function () {
                return has(s, 'restored') ? closeout() : Promise.resolve();
            }).catch(function () { event(s, 'recovery-unverified'); }).then(function () {
                clearInterval(timer); server.close();
            });
        }
    }, 25);
}
function transport(s, r) {
    return new Promise(function (resolve, reject) {
        var socket = net.connect(s.socket); socket.setTimeout(2400, function () { socket.destroy(new Error(REFUSAL)); });
        socket.on('connect', function () { socket.end(JSON.stringify(r)+'\n'); });
        framed(socket, 4096, function (error, value) { socket.destroy(); if (error) reject(new Error(REFUSAL)); else resolve(value); });
    });
}
function rpc(r) {
    var s = context(); request(s, r);
    if (r.phase === 'inspect') {
        var original = read(path.join(s.c.credentialDirectory, 'credentials.json'), 16384, [s.c.transactionProfile === 'private-0600' ? 384 : 438]);
        need(sha(original) === s.c.binding.beforeSha256); require(path.join(s.helper, 'transaction.js')).validateCredential(original);
        return profile(s, 'inspect', r.expiresAtUnixMs, {}).then(function () { return receipt(s, r, 'qualified'); });
    }
    if (r.phase !== 'prepare') return transport(s, r);
    need(!has(s, 'reserved')); claim(s, true); event(s, 'reserved');
    var child = cp.spawn(process.execPath, [__filename, '--daemon'], { detached: true, stdio: 'ignore', env: {} });
    var failed = false; child.on('error', function () { failed = true; }); child.unref();
    return new Promise(function (resolve, reject) {
        function wait() {
            try {
                request(s, r); need(!failed);
                if (has(s, 'ready')) return transport(s, r).then(resolve, reject);
                setTimeout(wait, 10);
            } catch (_) { reject(new Error(REFUSAL)); }
        } wait();
    });
}
function fail() { process.stderr.write(REFUSAL+'\n'); process.exitCode = 2; }
module.exports = { rpc: rpc };
if (require.main === module) {
    try {
        if (process.argv.length === 3 && process.argv[2] === '--daemon') daemon();
        else if (process.argv.length === 4 && process.argv[2] === '--consumer') consumer(process.argv[3]);
        else {
            need(process.argv.length === 2);
            framed(process.stdin, 65536, function (error, value) {
                if (error) return fail();
                try { rpc(value).then(function (reply) { process.stdout.write(JSON.stringify(reply)+'\n'); }).catch(fail); } catch (_) { fail(); }
            });
        }
    } catch (_) { fail(); }
}
