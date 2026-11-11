/* Test consumer only. No real service commands, hardware or network endpoints. */
'use strict';
var fs = require('fs'), path = require('path'), net = require('net'), crypto = require('crypto');
function need(ok) { if (!ok) throw new Error('Fixture consumer refused.'); }
function hash(root) { return crypto.createHash('sha256').update(fs.readFileSync(path.join(root, 'credentials.json'))).digest('hex'); }
function scope(root) { need(/^cra-[a-zA-Z0-9]+$/.test(path.basename(root)) && fs.realpathSync(root) === root && path.dirname(root) === require('os').tmpdir()); }
function rpc(root, value) {
    return new Promise(function (resolve, reject) {
        var client = net.connect(path.join(root, 'consumer.sock')), raw = '';
        client.setTimeout(1500, function () { client.destroy(new Error('Fixture deadline.')); });
        client.on('connect', function () { client.end(JSON.stringify(value)+'\n'); });
        client.on('data', function (b) { raw += b; if (raw.length > 4096) client.destroy(new Error('Fixture bound.')); });
        client.on('error', reject); client.on('end', function () { try { var reply = JSON.parse(raw); need(reply.ok === true); resolve(reply); } catch (e) { reject(e); } });
    });
}
module.exports.run = function (action, request, root) {
    scope(root);
    return rpc(root, { action: action, operationId: request.binding.operationId, expiresAtUnixMs: request.expiresAtUnixMs }).then(function (reply) {
        if (action === 'inspect' || action === 'observeOriginal' || action === 'reloadOriginal') need(reply.state.hash === request.binding.beforeSha256 && !reply.state.paused);
        if (action === 'reloadNew' || action === 'observeNew') need(reply.state.hash === request.binding.afterSha256 && !reply.state.paused);
        if (action === 'quiesce') need(reply.state.paused);
        if (action === 'fence') need(reply.state.fenced && reply.state.pending === 0);
        return true;
    });
};
if (require.main === module) {
    var root = process.argv[2]; scope(root);
    var state = { hash: hash(root), paused: false, fenced: false, pending: 0, newReloads: 0, originalReloads: 0 }, pending = [], fences = {}, currentOperation = '';
    var server = net.createServer({ allowHalfOpen: true }, function (client) {
        var raw = ''; client.on('error', function () {});
        client.on('data', function (b) { raw += b; if (raw.length > 4096) client.destroy(); });
        client.on('end', function () {
            function reply(ok) { var snapshot = JSON.parse(JSON.stringify(state)); snapshot.fenced = !!fences[r && r.operationId || currentOperation]; client.end(JSON.stringify({ ok: ok, state: snapshot })+'\n'); }
            try {
                var r = JSON.parse(raw), options = JSON.parse(fs.readFileSync(path.join(root, 'fixture-options.json')));
                if (r.action === 'state') return reply(true);
                if (r.action === 'stop') { reply(true); server.close(); return; }
                need(r.expiresAtUnixMs > Date.now());
                if (r.action === 'inspect' || r.action === 'observeNew' || r.action === 'observeOriginal') return reply(!options.failObserve);
                if (r.action === 'quiesce') {
                    need(!fences[r.operationId]); currentOperation = r.operationId; state.paused = true;
                    if (options.delayQuiesce) return setTimeout(function () { reply(!options.failQuiesce); }, options.delayQuiesce);
                    return reply(!options.failQuiesce);
                }
                if (r.action === 'fence') {
                    need(!options.failFence); fences[r.operationId] = true;
                    pending = pending.filter(function (p) { if (p.operationId !== r.operationId) return true; clearTimeout(p.timer); p.client.end('{"ok":false}\n'); return false; }); state.pending = pending.length; return reply(true);
                }
                if (r.action === 'reloadOriginal') { need(!options.failOriginal && fences[r.operationId]); state.hash = hash(root); state.paused = false; state.originalReloads++; return reply(true); }
                need(r.action === 'reloadNew' && !fences[r.operationId] && !options.failNew && state.paused);
                var item = { client: client, operationId: r.operationId };
                pending.push(item); state.pending++;
                item.timer = setTimeout(function () {
                    pending = pending.filter(function (p) { return p !== item; }); state.pending--;
                    if (fences[r.operationId] || r.expiresAtUnixMs <= Date.now()) return reply(false);
                    state.hash = hash(root); state.paused = false; state.newReloads++; reply(true);
                }, options.delayNew || 0);
            } catch (_) { reply(false); }
        });
    });
    server.listen(path.join(root, 'consumer.sock'), function () { fs.chmodSync(path.join(root, 'consumer.sock'), 384); process.stdout.write('ready\n'); });
}
