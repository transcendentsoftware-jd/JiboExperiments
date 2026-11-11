/* Local POSIX file transaction, ES5 / Node 6.9.2. No device transport or CLI. */
'use strict';
var fs = require('fs');
var path = require('path');
var crypto = require('crypto');
var LIMIT = 16384;
var REFUSAL = 'Credential delivery refused.';
function requireThat(ok) { if (!ok) throw new Error(REFUSAL); }
function sha(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }
function exact(value, keys) {
    requireThat(value && typeof value === 'object' && !Array.isArray(value) &&
        Object.keys(value).sort().join(' ') === keys.split(' ').sort().join(' '));
}
function validatePlan(p) {
    var stock = p && p.schema === 'openjibo.credential-delivery.v2';
    exact(p, 'schema operationId beforeSha256 afterSha256 mappingEvidenceSha256 recoveryEvidenceSha256' + (stock ? ' filePermissionProfile' : ''));
    requireThat((p.schema === 'openjibo.credential-delivery.v1' || stock) && typeof p.operationId === 'string' && /^[a-f0-9]{32}$/.test(p.operationId));
    if (stock) requireThat(p.filePermissionProfile === 'stock-root-0666-to-0600');
    ['beforeSha256', 'afterSha256', 'mappingEvidenceSha256', 'recoveryEvidenceSha256'].forEach(function (key) {
        requireThat(typeof p[key] === 'string' && /^[a-f0-9]{64}$/.test(p[key]));
    });
    requireThat(p.beforeSha256 !== p.afterSha256);
    var result = { schema: p.schema, operationId: p.operationId, beforeSha256: p.beforeSha256,
        afterSha256: p.afterSha256, mappingEvidenceSha256: p.mappingEvidenceSha256,
        recoveryEvidenceSha256: p.recoveryEvidenceSha256 };
    if (stock) result.filePermissionProfile = p.filePermissionProfile;
    return JSON.stringify(result);
}
function originalMode(p) { return p.schema === 'openjibo.credential-delivery.v2' ? 438 : 384; }
function sameFile(a, b) {
    return ['ino','dev','size','uid','gid','mode','nlink'].every(function (key) { return a[key] === b[key]; }) &&
        a.mtime.getTime() === b.mtime.getTime() && a.ctime.getTime() === b.ctime.getTime();
}
function credential(bytes) {
    requireThat(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= LIMIT);
    var text = bytes.toString('utf8');
    requireThat(Buffer.from(text, 'utf8').equals(bytes));
    // Tokenize a flat string object before JSON.parse so duplicate/escaped aliases
    // cannot be hidden by JSON's last-value-wins behavior. Nested values refuse.
    var pos = 0, seen = Object.create(null), value = Object.create(null);
    function space() { while (/[\x20\t\r\n]/.test(text.charAt(pos)) && pos < text.length) pos++; }
    function token() {
        space(); var match = /^"(?:[^"\\\x00-\x1f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"/.exec(text.slice(pos));
        requireThat(!!match); pos += match[0].length; return JSON.parse(match[0]);
    }
    space(); requireThat(text.charAt(pos++) === '{');
    while (true) {
        var key = token(); space(); requireThat(text.charAt(pos++) === ':');
        requireThat(!seen[key]); seen[key] = true; value[key] = token(); space();
        var end = text.charAt(pos++); if (end === '}') break;
        requireThat(end === ',');
    }
    space(); requireThat(pos === text.length);
    var keys = Object.keys(value).sort().join(' ');
    requireThat(keys === 'accessKeyId secretAccessKey' || keys === 'accessKeyId region secretAccessKey');
    requireThat(/^[!-~]{1,128}$/.test(value.accessKeyId) && !/[\/,]/.test(value.accessKeyId));
    requireThat(/^[!-~]{16,256}$/.test(value.secretAccessKey));
    if (value.region !== undefined) requireThat(value.region === 'api' || value.region === 'open-jibo');
    return value;
}
function create(root, io) {
    io = io || fs;
    // No Windows durability/permission emulation or implicit default robot path.
    requireThat(process.platform !== 'win32' && typeof process.getuid === 'function');
    requireThat(typeof root === 'string' && path.isAbsolute(root) && path.resolve(root) === root && root !== '/');
    requireThat(io.realpathSync(root) === root);
    var owner = io.lstatSync(root);
    requireThat(owner.isDirectory() && owner.uid === process.getuid() && (owner.mode & 18) === 0);
    var target = path.join(root, 'credentials.json'), lock = path.join(root, '.credential-delivery-lock');
    function syncDirectory(dir) {
        var fd = io.openSync(dir, 'r');
        try { io.fsyncSync(fd); } finally { io.closeSync(fd); }
    }
    function readWithMetadata(file, modes) {
        var st = io.lstatSync(file);
        requireThat(st.isFile() && st.nlink === 1 && st.uid === owner.uid && st.gid === owner.gid &&
            modes.indexOf(st.mode & 4095) >= 0 && st.size > 0 && st.size <= LIMIT);
        var fd = io.openSync(file, io.constants.O_RDONLY | io.constants.O_NOFOLLOW);
        try {
            var opened = io.fstatSync(fd);
            requireThat(sameFile(opened, st));
            var bytes = Buffer.alloc(LIMIT + 1), count = 0, n;
            while (count <= LIMIT) {
                n = io.readSync(fd, bytes, count, bytes.length - count, null);
                if (!n) break; count += n;
            }
            requireThat(count === st.size && count <= LIMIT);
            requireThat(sameFile(io.fstatSync(fd), st));
            return { bytes: bytes.slice(0, count), stat: st };
        } finally { io.closeSync(fd); }
    }
    function read(file, mode) { return readWithMetadata(file, [mode === undefined ? 384 : mode]).bytes; }
    function validateContext(p) {
        validatePlan(p);
        var now = io.lstatSync(root);
        requireThat(io.realpathSync(root) === root && now.isDirectory() &&
            ['uid','gid','ino','dev','mode'].every(function (key) { return now[key] === owner[key]; }));
        if (p.schema === 'openjibo.credential-delivery.v2')
            requireThat(owner.uid === 0 && owner.gid === 0 && process.getuid() === 0);
    }
    function current(p) {
        validateContext(p);
        var observed = readWithMetadata(target, [384, originalMode(p)]), hash = sha(observed.bytes);
        observed.hash = hash;
        observed.state = hash === p.beforeSha256 && (observed.stat.mode & 4095) === originalMode(p) ? 'staged' :
            hash === p.afterSha256 && (observed.stat.mode & 4095) === 384 ? 'installed' : 'diverged';
        return observed;
    }
    function write(file, bytes, mode) {
        mode = mode === undefined ? 384 : mode;
        var fd = io.openSync(file, 'wx', 384);
        try {
            var at = 0;
            while (at < bytes.length) {
                var n = io.writeSync(fd, bytes, at, bytes.length - at, null);
                requireThat(n > 0); at += n;
            }
            if (mode !== 384) io.fchmodSync(fd, mode);
            var st = io.fstatSync(fd);
            requireThat(st.uid === owner.uid && st.gid === owner.gid && (st.mode & 4095) === mode && st.nlink === 1);
            io.fsyncSync(fd);
        } finally { io.closeSync(fd); }
    }
    function guarded(fn) {
        var acquired = false, result;
        try {
            // A crashed writer leaves this lock in place. Never steal/break it.
            io.mkdirSync(lock, 448); acquired = true; syncDirectory(root);
            result = fn();
        } catch (_) { throw new Error(REFUSAL); }
        finally { if (acquired) { io.rmdirSync(lock); syncDirectory(root); } }
        result.lockPresent = false;
        return result;
    }
    function directory(p) { validateContext(p); return path.join(root, '.credential-delivery-' + p.operationId); }
    function loaded(p) {
        var dir = directory(p), st = io.lstatSync(dir);
        requireThat(st.isDirectory() && st.uid === owner.uid && st.gid === owner.gid && st.dev === owner.dev && (st.mode & 511) === 448);
        requireThat(read(path.join(dir, 'plan.json')).toString('utf8') === validatePlan(p) + '\n');
        var before = read(path.join(dir, 'before.bin')), after = read(path.join(dir, 'after.bin'));
        credential(before); credential(after);
        requireThat(sha(before) === p.beforeSha256 && sha(after) === p.afterSha256);
        return { dir: dir, before: before, after: after };
    }
    function exists(file) { try { io.lstatSync(file); return true; } catch (e) { if (e.code === 'ENOENT') return false; throw e; } }
    function report(p, data) {
        var observed = current(p), terminal = path.join(data.dir, 'restored.json');
        var restored = exists(terminal);
        if (restored) requireThat(read(terminal).toString('utf8') === '{"restored":true}\n' && observed.state === 'staged');
        var result = { schema: 'openjibo.credential-delivery-status.v1',
            state: restored ? 'restored' : observed.state,
            lockPresent: exists(lock), ownershipVerified: false, recoveryVerified: false,
            robotContactPerformed: false, serviceAccessGranted: false };
        if (p.schema === 'openjibo.credential-delivery.v2') result.filePermissionProfile = p.filePermissionProfile;
        return result;
    }
    function replace(p, data, bytes, expectedHash, tempName, mode) {
        var initial = current(p);
        requireThat(initial.hash === expectedHash && initial.state !== 'diverged');
        var temp = path.join(data.dir, tempName);
        // Preserve partial files. A complete pre-rename file may be resumed only
        // when its bytes exactly match the reviewed transaction.
        if (exists(temp)) requireThat(sha(read(temp, mode)) === sha(bytes)); else write(temp, bytes, mode);
        syncDirectory(data.dir);
        var beforeRename = current(p);
        requireThat(beforeRename.hash === expectedHash && beforeRename.state !== 'diverged' && sameFile(beforeRename.stat, initial.stat));
        io.renameSync(temp, target);
        syncDirectory(root); syncDirectory(data.dir);
        requireThat(sha(read(target, mode)) === sha(bytes));
    }
    function safe(fn) { return function () { try { return fn.apply(null, arguments); } catch (_) { throw new Error(REFUSAL); } }; }
    return {
        prepare: safe(function (p, replacement) {
            validateContext(p); credential(replacement); requireThat(sha(replacement) === p.afterSha256);
            return guarded(function () {
                if (exists(directory(p))) return report(p, loaded(p));
                var observed = current(p), original = observed.bytes;
                credential(original); requireThat(observed.state === 'staged');
                // Never silently change routing as part of credential delivery.
                requireThat(credential(original).region === credential(replacement).region);
                requireThat(credential(original).accessKeyId !== credential(replacement).accessKeyId ||
                    credential(original).secretAccessKey !== credential(replacement).secretAccessKey);
                io.mkdirSync(directory(p), 448); syncDirectory(root);
                write(path.join(directory(p), 'before.bin'), original);
                write(path.join(directory(p), 'after.bin'), replacement);
                write(path.join(directory(p), 'plan.json'), Buffer.from(validatePlan(p) + '\n', 'utf8'));
                syncDirectory(directory(p)); syncDirectory(root);
                var afterBackup = current(p);
                requireThat(afterBackup.state === 'staged' && sameFile(afterBackup.stat, observed.stat));
                return report(p, loaded(p));
            });
        }),
        inspect: safe(function (p) { return report(p, loaded(p)); }),
        activate: safe(function (p) { validateContext(p); return guarded(function () {
            var data = loaded(p), status = report(p, data);
            requireThat(status.state === 'staged' || status.state === 'installed');
            if (status.state === 'staged') replace(p, data, data.after, p.beforeSha256, 'activate.tmp', 384);
            return report(p, data);
        }); }),
        restore: safe(function (p) { validateContext(p); return guarded(function () {
            var data = loaded(p), status = report(p, data);
            requireThat(status.state !== 'diverged');
            if (status.state === 'installed') replace(p, data, data.before, p.afterSha256, 'restore.tmp', originalMode(p));
            if (status.state !== 'restored') {
                write(path.join(data.dir, 'restored.json'), Buffer.from('{"restored":true}\n'));
                syncDirectory(data.dir);
            }
            return report(p, data);
        }); })
    };
}
module.exports = { create: function (root, io) {
    try { return create(root, io); } catch (_) { throw new Error(REFUSAL); }
}, validateCredential: credential, sha256: sha };
if (require.main === module) {
    process.stderr.write('Credential delivery has no standalone execution interface.\n'); process.exitCode = 2;
}
