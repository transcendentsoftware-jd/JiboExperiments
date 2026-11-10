/* Read-only, fixed-path inventory. ES5 syntax supports the stock robot Node. */
'use strict';
var fs = require('fs');
var MAX_BYTES = 65536;
var FILES = {
    ssm: '/usr/local/bin/jibo-ssm/package.json',
    jetstream: '/usr/local/etc/jibo-jetstream-service.json',
    server: '/usr/local/etc/jibo-server-service.json',
    credentials: '/var/jibo/credentials.json'
};

function inspect(io) {
    function regular(path) {
        try { return io.statSync(path).isFile(); } catch (_) { return false; }
    }
    function json(path) {
        var fd;
        try {
            fd = io.openSync(path, 'r');
            var st = io.fstatSync(fd);
            if (!st.isFile() || st.size > MAX_BYTES) return null;
            // Read at most one extra byte even if a file grows after fstat.
            var buffer = typeof Buffer.alloc === 'function' ? Buffer.alloc(MAX_BYTES + 1) : new Buffer(MAX_BYTES + 1);
            var count = 0, n;
            while (count <= MAX_BYTES) {
                n = io.readSync(fd, buffer, count, buffer.length - count, null);
                if (n === 0) break;
                count += n;
            }
            if (count > MAX_BYTES) return null;
            return JSON.parse(buffer.toString('utf8', 0, count));
        } catch (_) { return null; }
        finally { if (fd !== undefined) io.closeSync(fd); }
    }
    function textVersion(value) {
        return typeof value === 'string' && /^\d+(?:\.\d+){1,3}(?:[-+][A-Za-z0-9.-]{1,32})?$/.test(value) ? value : null;
    }
    function host(value) {
        return typeof value === 'string' && value.length <= 253 &&
            /^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/.test(value) ? value : null;
    }
    var ssm = json(FILES.ssm), jet = json(FILES.jetstream), server = json(FILES.server);
    var settings = jet && jet.HubClient && jet.HubClient['region-settings'];
    var regions = [];
    // These are configured candidates, never an inferred active region.
    ['api', 'open-jibo'].forEach(function (name) {
        var item = settings && settings[name];
        if (item && typeof item === 'object') regions.push({ region: name,
            apiHostname: host(item.entrypoint_hostname), hubHostname: host(item.hub_hostname) });
    });
    var suffix = server && server.NotificationSubsystem && server.NotificationSubsystem.serverURLSuffix;
    if (typeof suffix !== 'string' || !/^-[A-Za-z0-9.-]{1,100}$/.test(suffix)) suffix = null;
    return {
        schema: 'openjibo.robot-runtime-inventory.v1',
        components: { systemManagerPackageVersion: textVersion(ssm && ssm.version) },
        files: { jetstreamConfigPresent: regular(FILES.jetstream),
            serverConfigPresent: regular(FILES.server), credentialsFilePresent: regular(FILES.credentials) },
        configuration: { regionCandidates: regions, notificationHostnameSuffix: suffix },
        firmwareReleaseVerified: false, activeRoutingVerified: false,
        credentialIdentityVerified: false, conversionReady: false,
        credentialsRead: false, writesPerformed: false
    };
}

module.exports = { inspect: inspect };
if (require.main === module) {
    if (process.argv.length !== 2) {
        process.stderr.write('Runtime inventory takes no arguments.\n'); process.exitCode = 2;
    } else {
        process.stdout.write(JSON.stringify(inspect(fs)) + '\n');
    }
}
