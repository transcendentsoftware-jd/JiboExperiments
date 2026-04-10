    "use strict";

    console.log("SERVER_VERSION", "2026-04-04-jibo-fake-cloud-v8-STT");

    const os = require("os");
    const { spawn } = require("child_process");

    const fs = require("fs");
    const path = require("path");
    const https = require("https");
    const tls = require("tls");
    const crypto = require("crypto");
    const { WebSocketServer, WebSocket } = require("ws");

    const HOST = "0.0.0.0";
    const PORT = 443;

    const key = fs.readFileSync("./key.pem");
    const cert = fs.readFileSync("./cert.pem");

    const LOG_DIR = path.join(__dirname, "logs");
    fs.mkdirSync(LOG_DIR, { recursive: true });

    const state = {
      tokens: new Map(),
      hubTokens: new Map(),
      hubSessions: new Map(),
      symmetricKeys: new Map(),
      requests: new Map(),

      account: {
        id: "usr_test_001",
        email: "jibo@example.com",
        firstName: "Jibo",
        lastName: "Owner",
        gender: "unknown",
        birthday: 631152000000,
        phoneNumber: "+10000000000",
        photoUrl: "",
        isActive: true,
        messagingAllowed: true,
        accessKeyId: "fake-access-key-id",
        secretAccessKey: "fake-secret-access-key",
        roles: [],
        facebookConnected: false,
        termsAccepted: true
      },

      robot: {
        id: "my-robot-name",
        payload: {}
      },

      robotCreated: null,

      loops: [
        {
          id: "fake-loop-id",
          name: "OpenJibo Test Loop",
          owner: "usr_test_001",
          robot: "my-robot-name",
          robotFriendlyId: "my-robot-serial-number",
          members: [],
          isSuspended: false,
          created: 1775099000000,
          updated: 1775099000000
        }
      ],

      media: [],

      updates: []
      
    };

    const SSID = "my-ssid";
    const TLS_DEBUG = false;
    const HTTP_BODY_CONSOLE_LIMIT = 1200;
    const WS_TEXT_CONSOLE_LIMIT = 4000;
    const WS_BINARY_HEX_PREVIEW_BYTES = 128;
    const LOG_BINARY_UPLOAD_PREVIEW = true;
    const MAX_HUB_SESSIONS = 250;
    
    const JOKES = [
      "Why did the robot cross the road? Because it was programmed by the chicken.",
      "Why was the robot tired when it got home? It had a hard drive.",
      "What do you call a pirate robot? Arrrr two dee two.",
      "Why did the robot go on vacation? It needed to recharge.",
      "What is a robots favorite kind of music? Heavy metal.",
      "Why did the robot blush? Because it saw the computers motherboard.",
      "What do you call a robot that takes the long way around? R two detour.",
      "Why did the robot bring a ladder? Because it wanted to reach the cloud.",
      "What do robots eat for snacks? Microchips.",
      "Why are robots bad at secrets? Because they always leak data.",
      "Why dont some couples go to the gym? Because some relationships dont work out.",
      "Why did the coffee file a police report? Because it got mugged!",
      "What did the grape say when it got stepped on? Nothing, it just let out a little wine.",
      "Why did the astronaut break up with his girlfriend? He needed space.",
      "Why was the math book sad? Because it had too many problems.",
      "Why did the scarecrow win an award? Because he was outstanding in his field.",
      "Why dont eggs tell jokes? Theyd crack each other up.",
      "What do you call a can<break size=\"0.5\" /> opener that doesnt work? A cant opener.",
      "Why did the computer go to the doctor? It had a virus.",
      "Why did the kid bring a ladder to school? He wanted to reach his full potential.",
      "What do you call a dog that does magic tricks? A labracadabrador.",
      "Why did the chicken cross the playground? To get to the other slide.",
      "Why did the bicycle fall over? Because it was two-tired.",
      "What do you call a pile of cats? A meow-n-tin.",
      "What kind of shoes do frogs wear? Open-toed."
    ];

    const ASR_ENABLED = true;
    const ASR_MAX_TURN_MS = 1800;
    const ASR_MIN_AUDIO_FRAMES = 5;
    const ASR_MIN_AUDIO_BYTES = 12000;
    const ASR_FINALIZE_GRACE_MS = 1200;
    const ASR_FINALIZE_POLL_MS = 250;
    const ASR_MIN_GOOD_WAV_BYTES = 2000;
    const ASR_EARLY_START_MS = 900;
    const ASR_EARLY_MIN_FRAMES = 4;
    const ASR_EARLY_MIN_BYTES = 14000;
    const ASR_DEBUG_AUDIO = false;
    const ASR_DEBUG_OGG = false;
    
    // External tools
    const FFMPEG_BIN = process.env.FFMPEG_BIN || "/usr/bin/ffmpeg";
    const WHISPER_CPP_BIN = process.env.WHISPER_CPP_BIN || "/usr/bin/whisper.cpp/build/bin/whisper-cli";
    const WHISPER_MODEL = process.env.WHISPER_MODEL || "/usr/bin/whisper.cpp/models/ggml-base.en.bin";

    // temp storage
    const ASR_TMP_DIR = path.join(os.tmpdir(), "openjibo-asr");
    fs.mkdirSync(ASR_TMP_DIR, { recursive: true });
    
    function logTiming(event, extra = {}) {
      const payload = {
        at: nowIso(),
        event,
        ...extra
      };
      console.log("TIMING", JSON.stringify(payload));
      writeStructuredLog(`timing_${event}`, payload);
    }

    function randomItem(items) {
      if (!Array.isArray(items) || items.length === 0) return "";
      return items[Math.floor(Math.random() * items.length)];
    }

    function escapeXml(value) {
      return String(value || "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
    }

    function sendWsJson(ws, payload, logPrefix = null) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return false;

      if (logPrefix) {
        writeStructuredLog(logPrefix, {
          at: nowIso(),
          payload
        });
      }

      ws.send(JSON.stringify(payload));
      return true;
    }

    function nowIso() {
      return new Date().toISOString();
    }

    function makeReqId() {
      return `${Date.now()}-${crypto.randomBytes(4).toString("hex")}`;
    }

    function safeJsonParse(buf) {
      try {
        return JSON.parse(buf.toString("utf8"));
      } catch {
        return null;
      }
    }

    function sanitizeForFilename(value) {
      return String(value || "unknown").replace(/[^a-zA-Z0-9._-]+/g, "_");
    }

    function writeStructuredLog(prefix, payload) {
      try {
        const stamp = new Date().toISOString().replace(/[:.]/g, "-");
        const name = `${stamp}_${sanitizeForFilename(prefix)}.json`;
        fs.writeFileSync(
          path.join(LOG_DIR, name),
          JSON.stringify(payload, null, 2),
          "utf8"
        );
      } catch (err) {
        console.error("Failed to write structured log:", err);
      }
    }

    function readBody(req) {
      return new Promise((resolve, reject) => {
        const chunks = [];
        req.on("data", (c) => chunks.push(c));
        req.on("end", () => resolve(Buffer.concat(chunks)));
        req.on("error", reject);
      });
    }

    function getTargetParts(req) {
      const raw = req.headers["x-amz-target"] || "";
      const parts = raw.split(".");
      return {
        raw,
        servicePrefix: parts[0] || "",
        operation: parts[1] || ""
      };
    }

    function getHost(req) {
      return (req.headers.host || "").split(":")[0].toLowerCase();
    }

    function summarizeUtf8(value, limit = HTTP_BODY_CONSOLE_LIMIT) {
      if (!value) return "";
      if (value.length <= limit) return value;
      return `${value.slice(0, limit)}\n...[truncated ${value.length - limit} chars]`;
    }

    function hexPreview(buffer, maxBytes = 64) {
      if (!buffer || !buffer.length) return "";
      return buffer.subarray(0, Math.min(buffer.length, maxBytes)).toString("hex");
    }

    function looksMostlyText(buffer) {
      if (!buffer || !buffer.length) return true;
      let printable = 0;
      const sample = buffer.subarray(0, Math.min(buffer.length, 256));
      for (const b of sample) {
        if (
          b === 9 ||
          b === 10 ||
          b === 13 ||
          (b >= 32 && b <= 126)
        ) {
          printable++;
        }
      }
      return printable / sample.length > 0.85;
    }

    function buildRequestRecord(req, bodyBuffer) {
      const parsed = safeJsonParse(bodyBuffer);
      const target = getTargetParts(req);
      const host = getHost(req);

      return {
        reqId: makeReqId(),
        at: nowIso(),
        host,
        method: req.method,
        url: req.url,
        headers: req.headers,
        target,
        bodyLength: bodyBuffer.length,
        bodyLooksText: looksMostlyText(bodyBuffer),
        bodyUtf8: bodyBuffer.length ? bodyBuffer.toString("utf8") : "",
        bodyHexPreview: bodyBuffer.length ? hexPreview(bodyBuffer, 128) : "",
        bodyJson: parsed
      };
    }

    function consoleBanner(record) {
      console.log("==== HTTPS REQUEST ====");
      console.log("ReqId:", record.reqId);
      console.log("Time:", record.at);
      console.log("Host:", record.host);
      console.log("Method:", record.method);
      console.log("URL:", record.url);
      console.log("X-Amz-Target:", record.target.raw || "<none>");
      console.log("BodyLength:", record.bodyLength);
      console.log("Headers:", JSON.stringify(record.headers, null, 2));

      if (!record.bodyLength) {
        console.log("Body: <empty>");
      } else if (record.bodyLooksText) {
        console.log("Body (utf8):", summarizeUtf8(record.bodyUtf8));
      } else {
        console.log("Body: <binary>");
        console.log("Body HEX preview:", record.bodyHexPreview);
      }

      console.log("=======================");
    }

    function respondPlanned(res, plan) {
      if (Object.prototype.hasOwnProperty.call(plan, "rawBody")) {
        const body = plan.rawBody || "";
        res.writeHead(plan.statusCode, {
          "Content-Length": Buffer.byteLength(body),
          Connection: "close",
          ...plan.extraHeaders
        });
        return res.end(body);
      }

      const body = JSON.stringify(plan.body ?? {});
      res.writeHead(plan.statusCode, {
        "Content-Type": "application/x-amz-json-1.1",
        "Content-Length": Buffer.byteLength(body),
        Connection: "keep-alive",
        ...plan.extraHeaders
      });
      res.end(body);
    }

    function makeToken(deviceId) {
      const token = `token-${deviceId}-${Date.now()}`;
      state.tokens.set(token, {
        deviceId,
        accountId: state.account.id,
        createdAt: nowIso()
      });
      return token;
    }

    function makeHubToken() {
      const token = `hub-${state.account.id}-${Date.now()}`;
      state.hubTokens.set(token, {
        accountId: state.account.id,
        robotId: state.robot.id,
        createdAt: nowIso()
      });
      return token;
    }

    function getDeviceIdFromBody(parsed) {
      if (!parsed || typeof parsed !== "object") {
        return "unknown-device";
      }
      return (
        parsed.deviceId ||
        parsed.serial_number ||
        parsed.serialNumber ||
        parsed.cpuid ||
        parsed.cpuId ||
        parsed.robotId ||
        "unknown-device"
      );
    }

    function getLoopId(parsed) {
      return parsed?.loopId || parsed?.id || state.loops[0].id;
    }

    function getOrCreateSymmetricKey(loopId) {
      if (!state.symmetricKeys.has(loopId)) {
        const keyMaterial = Buffer.from(`open-jibo-symmetric-key:${loopId}`).toString("base64");
        state.symmetricKeys.set(loopId, keyMaterial);
      }
      return state.symmetricKeys.get(loopId);
    }

    function getBearerToken(request) {
      const auth = request.headers.authorization || "";
      const m = auth.match(/^Bearer\s+(.+)$/i);
      return m ? m[1] : null;
    }

    function classifyWebSocket(request) {
      const host = (request.headers.host || "").split(":")[0].toLowerCase();
      const url = request.url || "/";
      const pathToken = url.replace(/^\//, "");
      const bearerToken = getBearerToken(request);

      let kind = "unknown";
      if (host === "api-socket.jibo.com") {
        kind = "api-socket";
      } else if (host === "neo-hub.jibo.com") {
        kind = url.startsWith("/v1/proactive")
          ? "neo-hub-proactive"
          : "neo-hub-listen";
      }

      return {
        host,
        url,
        kind,
        pathToken,
        bearerToken,
        pathTokenKnown: state.tokens.has(pathToken),
        bearerTokenKnown: bearerToken ? state.hubTokens.has(bearerToken) : false,
        transId: request.headers["x-jibo-transid"] || null,
        robotId: request.headers["x-jibo-robotid"] || null
      };
    }

    function handleAccountOperation(operation, parsed) {
      console.log("ACCOUNT OPERATION:", operation, "BODY:", JSON.stringify(parsed || {}));

      if (operation === "CreateHubToken") {
        const expires = Date.now() + 60 * 60 * 1000;

        return {
          statusCode: 200,
          note: "Issued CreateHubToken",
          body: {
            token: makeHubToken(),
            expires
          }
        };
      }

      if (operation === "CreateAccessToken") {
        const expires = Date.now() + 60 * 60 * 1000;

        return {
          statusCode: 200,
          note: "Issued CreateAccessToken",
          body: {
            token: `access-${state.account.id}-${Date.now()}`,
            expires
          }
        };
      }

      if (operation === "CheckEmail") {
        const email = parsed?.email || "";
        return {
          statusCode: 200,
          note: "Checked email",
          body: {
            exists: email.toLowerCase() === String(state.account.email).toLowerCase()
          }
        };
      }

      if (operation === "Create" || operation === "Login") {
        const email = parsed?.email || state.account.email;
        const firstName = parsed?.firstName || state.account.firstName;
        const lastName = parsed?.lastName || state.account.lastName;

        return {
          statusCode: 200,
          note: `Handled account ${operation}`,
          body: {
            ...state.account,
            email,
            firstName,
            lastName
          }
        };
      }

      if (operation === "Get") {
        const ids = Array.isArray(parsed?.ids) ? parsed.ids : null;
        const matches = !ids || ids.length === 0 || ids.includes(state.account.id);

        return {
          statusCode: 200,
          note: "Returned account list",
          body: matches ? [{ ...state.account }] : []
        };
      }

      if (
        operation === "Update" ||
        operation === "ResetKeys" ||
        operation === "Remove" ||
        operation === "ActivateByCode" ||
        operation === "ResendActivationCode" ||
        operation === "ChangePassword" ||
        operation === "SendPasswordReset" ||
        operation === "PasswordResetByCode" ||
        operation === "UpdatePhoto" ||
        operation === "RemovePhoto" ||
        operation === "VerifyPhoneByCode" ||
        operation === "AcceptTerms" ||
        operation === "FacebookConnect" ||
        operation === "FacebookMobileConnect"
      ) {
        return {
          statusCode: 200,
          note: `Handled account ${operation}`,
          body: {
            ...state.account
          }
        };
      }

      if (operation === "ChangeEmail" || operation === "SendPhoneVerificationCode") {
        return {
          statusCode: 200,
          note: `Handled account ${operation}`,
          body: {
            id: state.account.id
          }
        };
      }

      if (operation === "GetAccountByAccessToken") {
        return {
          statusCode: 200,
          note: "Returned account by access token",
          body: {
            id: state.account.id,
            accessKeyId: state.account.accessKeyId,
            secretAccessKey: state.account.secretAccessKey,
            email: state.account.email,
            friendlyId: state.robot.id,
            payload: parsed?.payload || {}
          }
        };
      }

      if (operation === "Search") {
        const query = String(parsed?.query || "").toLowerCase();
        const haystack = [
          state.account.email,
          state.account.firstName,
          state.account.lastName,
          state.account.id
        ].join(" ").toLowerCase();

        return {
          statusCode: 200,
          note: "Returned account search results",
          body: query && haystack.includes(query) ? [{ ...state.account }] : []
        };
      }

      if (operation === "FacebookPrepareLogin") {
        return {
          statusCode: 200,
          note: "Prepared Facebook login",
          body: {
            url: "https://example.com/facebook-login",
            client_id: "fake-client-id",
            scope: "email",
            response_type: "token",
            state: `fb-${Date.now()}`,
            redirect_uri: "https://api.jibo.com/facebook/callback"
          }
        };
      }

      if (operation === "ConfirmEmailReset") {
        return {
          statusCode: 200,
          note: "Confirmed email reset",
          body: {}
        };
      }

      return {
        statusCode: 200,
        note: "Default account handler",
        body: {
          ...state.account
        }
      };
    }

    function handleNotificationOperation(operation, parsed) {
      if (operation === "NewRobotToken") {
        const deviceId = getDeviceIdFromBody(parsed);
        const token = makeToken(deviceId);
        return {
          statusCode: 200,
          note: "Issued robot token",
          body: { token }
        };
      }

      return {
        statusCode: 200,
        note: "Default notification handler",
        body: { ok: true, operation }
      };
    }

    function handleLoopOperation(operation) {
      console.log("LOOP OPERATION:", operation);

      if (operation === "List" || operation === "ListLoops") {
        return {
          statusCode: 200,
          note: "Returned one loop",
          body: state.loops
        };
      }

      return {
        statusCode: 200,
        note: "Default loop handler",
        body: []
      };
    }

    function handleLogOperation(operation, parsed) {
      if (operation === "PutEventsAsync") {
        return {
          statusCode: 200,
          note: "Accepted log events async",
          body: {
            contentEncoding: "gzip",
            uploadUrl: "https://api.jibo.com/upload/log-events"
          }
        };
      }

      if (operation === "PutEvents") {
        return {
          statusCode: 200,
          note: "Accepted inline log events",
          body: {}
        };
      }

      if (operation === "PutBinaryAsync") {
        return {
          statusCode: 200,
          note: "Accepted binary async request",
          body: {
            url: "https://api.jibo.com/log/binary/fake-id",
            uploadUrl: "https://api.jibo.com/upload/log-binary"
          }
        };
      }

      if (operation === "PutAsrBinary") {
        logTiming("put_asr_binary_request", {
          body: parsed || {}
        });
        return {
          statusCode: 200,
          note: "Accepted ASR binary request",
          body: {
            bucketName: "openjibo-test",
            key: "asr/fake-key",
            uploadUrl: "https://api.jibo.com/upload/asr-binary"
          }
        };
      }

      if (operation === "NewKinesisCredentials") {
        return {
          statusCode: 200,
          note: "Returned fake kinesis credentials",
          body: {
            credentials: {
              AccessKeyId: "fake-access-key",
              Expiration: new Date(Date.now() + 3600 * 1000).toISOString(),
              SecretAccessKey: "fake-secret",
              SessionToken: "fake-session"
            },
            region: "us-east-1",
            streamName: "openjibo-log-stream"
          }
        };
      }

      return {
        statusCode: 200,
        note: "Default log handler",
        body: {}
      };
    }

    function handleMediaOperation(operation, parsed, record) {
      if (operation === "List") {
        const loopIds = Array.isArray(parsed?.loopIds) ? parsed.loopIds : [];
        const after = typeof parsed?.after === "number" ? parsed.after : null;
        const before = typeof parsed?.before === "number" ? parsed.before : null;

        const items = state.media.filter((item) => {
          if (loopIds.length && !loopIds.includes(item.loopId)) return false;
          if (after != null && !(item.created > after)) return false;
          if (before != null && !(item.created < before)) return false;
          return true;
        });

        return {
          statusCode: 200,
          note: `Returned ${items.length} media items`,
          body: items
        };
      }

      if (operation === "Get") {
        const paths = Array.isArray(parsed?.paths) ? parsed.paths : [];
        const items = state.media.filter((item) => paths.includes(item.path));

        return {
          statusCode: 200,
          note: `Returned ${items.length} requested media items`,
          body: items
        };
      }

      if (operation === "Remove") {
        const paths = Array.isArray(parsed?.paths) ? parsed.paths : [];

        state.media = state.media.map((item) =>
          paths.includes(item.path)
            ? { ...item, isDeleted: true }
            : item
        );

        const items = state.media.filter((item) => paths.includes(item.path));

        return {
          statusCode: 200,
          note: `Marked ${items.length} media items deleted`,
          body: items
        };
      }

      if (operation === "Create") {
        const created = Date.now();

        const loopId =
          record.headers["x-loop-id"] ||
          parsed?.loopId ||
          state.loops[0].id;

        const mediaPath =
          record.headers["x-path"] ||
          parsed?.path ||
          `/media/${created}`;

        const mediaType =
          record.headers["x-type"] ||
          parsed?.type ||
          "unknown";

        const reference =
          record.headers["x-reference"] ||
          parsed?.reference ||
          "";

        const encryptedHeader = record.headers["x-encrypted"];
        const isEncrypted =
          encryptedHeader === true ||
          encryptedHeader === "true" ||
          parsed?.isEncrypted === true;

        let meta = parsed?.meta || {};

        // Optional: support x-meta as JSON string if Jibo ever sends it that way
        if ((!meta || Object.keys(meta).length === 0) && record.headers["x-meta"]) {
          try {
            meta = JSON.parse(record.headers["x-meta"]);
          } catch {
            meta = {};
          }
        }

        const item = {
          path: mediaPath,
          created,
          type: mediaType,
          reference,
          accountId: state.account.id,
          loopId,
          url: `https://api.jibo.com/media/${encodeURIComponent(mediaPath)}`,
          isEncrypted,
          isDeleted: false,
          meta
        };

        state.media.push(item);

        return {
          statusCode: 200,
          note: "Created media item",
          body: item
        };
      }

      return {
        statusCode: 200,
        note: "Default media handler",
        body: []
      };
    }

    function handlePersonOperation(operation) {
      if (operation === "ListHolidays") {
        return {
          statusCode: 200,
          note: "Returned 1 holidays",
          body: [
          {
              "id": "easter-1",
              "eventId": null,
              "name": "Easter",
              "category": "holiday",
              "subcategory": null,
              "loopId": state.loops[0].id,
              "memberId": null,
              "isEnabled": true,
              "date": "2026-04-05",
              "endDate": null,
              "created": nowIso()
            }
          ]
        };
      }

      return {
        statusCode: 200,
        note: "Default person handler",
        body: []
      };
    }

    function handleBackupOperation(operation) {
      if (operation === "List") {
        return {
          statusCode: 200,
          note: "No backups available",
          body: []
        };
      }

      return {
        statusCode: 200,
        note: "Default backup handler",
        body: []
      };
    }

    function handleKeyOperation(operation, parsed) {
      const loopId = getLoopId(parsed);

      if (operation === "ShouldCreate") {
        return {
          statusCode: 200,
          note: "Allow local symmetric key creation",
          body: { shouldCreate: true }
        };
      }

      if (operation === "CreateSymmetricKey") {
        const symmetricKey = getOrCreateSymmetricKey(loopId);
        return {
          statusCode: 200,
          note: "Created symmetric key",
          body: {
            loopId,
            key: symmetricKey,
            symmetricKey,
            created: true
          }
        };
      }

      if (operation === "CreateRequest" || operation === "RequestSymmetricKey") {
        const id = `req-${Date.now()}`;
        const requestRecord = {
          id,
          loopId,
          publicKey: parsed?.publicKey || "",
          encryptedKey: "",
          createdAt: nowIso()
        };
        state.requests.set(id, requestRecord);

        return {
          statusCode: 200,
          note: "Accepted symmetric key request",
          body: {
            id,
            loopId
          }
        };
      }

      if (operation === "GetRequest") {
        const id = parsed?.id;
        const requestRecord = state.requests.get(id) || {
          id: id || "unknown-request",
          loopId,
          publicKey: parsed?.publicKey || "",
          encryptedKey: ""
        };

        return {
          statusCode: 200,
          note: "Returned request record",
          body: requestRecord
        };
      }

      if (operation === "ListIncomingRequests") {
        return {
          statusCode: 200,
          note: "No incoming key requests",
          body: []
        };
      }

      if (operation === "ListBinaryRequests") {
        return {
          statusCode: 200,
          note: "No incoming binary requests",
          body: []
        };
      }

      if (operation === "Share" || operation === "ShareSymmetricKey") {
        return {
          statusCode: 200,
          note: "Accepted shared symmetric key",
          body: { ok: true }
        };
      }

      if (operation === "ShareBinary") {
        return {
          statusCode: 200,
          note: "Accepted shared binary",
          body: { ok: true }
        };
      }

      if (operation === "LoadSymmetricKey") {
        const symmetricKey = state.symmetricKeys.get(loopId) || "";
        return {
          statusCode: 200,
          note: "Returned cached symmetric key if present",
          body: {
            loopId,
            key: symmetricKey,
            symmetricKey
          }
        };
      }

      return {
        statusCode: 200,
        note: "Default key handler",
        body: { ok: true, operation }
      };
    }

    function normalizeUpdate(update) {
      return {
        _id: update._id,
        created: update.created,
        accountId: update.accountId,
        fromVersion: update.fromVersion,
        toVersion: update.toVersion,
        changes: update.changes,
        url: update.url,
        shaHash: update.shaHash,
        length: update.length ?? 0,
        subsystem: update.subsystem,
        filter: update.filter ?? null,
        dependencies: update.dependencies ?? {}
      };
    }

    function findMatchingUpdates(parsed) {
      const fromVersion = parsed?.fromVersion || null;
      const subsystem = parsed?.subsystem || null;
      const filter = parsed?.filter || null;

      return state.updates.filter((u) => {
        if (fromVersion && u.fromVersion !== fromVersion) return false;
        if (subsystem && u.subsystem !== subsystem) return false;
        if (filter && u.filter !== filter) return false;
        return true;
      });
    }

    function handleUpdateOperation(operation, parsed) {
      console.log("UPDATE OPERATION:", operation, "BODY:", JSON.stringify(parsed || {}));

      if (operation === "ListUpdates") {
        const subsystem = parsed?.subsystem || null;
        const filter = parsed?.filter || null;

        const matches = state.updates.filter((u) => {
          if (subsystem && u.subsystem !== subsystem) return false;
          if (filter && u.filter !== filter) return false;
          return true;
        });

        return {
          statusCode: 200,
          note: `Returned ${matches.length} updates`,
          body: matches.map(normalizeUpdate)
        };
      }

      if (operation === "ListUpdatesFrom") {
        const matches = findMatchingUpdates(parsed);

        return {
          statusCode: 200,
          note: `Returned ${matches.length} matching updates`,
          body: matches.map(normalizeUpdate)
        };
      }

      if (operation === "GetUpdateFrom") {
        const match = findMatchingUpdates(parsed)[0];

        if (match) {
          return {
            statusCode: 200,
            note: "Returned matching update",
            body: normalizeUpdate(match)
          };
        }

        const fromVersion = parsed?.fromVersion || "unknown";
        const subsystem = parsed?.subsystem || "unknown";
        const filter = parsed?.filter || null;
        const created = Date.now();

        return {
          statusCode: 200,
          note: "Returned placeholder no-op update",
          body: {
            _id: `noop-update-${subsystem}-${fromVersion}`,
            created,
            accountId: state.account.id,
            fromVersion,
            toVersion: fromVersion,
            changes: "No update available",
            url: "https://api.jibo.com/update/noop",
            shaHash: "noop",
            length: 0,
            subsystem,
            filter,
            dependencies: {}
          }
        };
      }

      if (operation === "CreateUpdate") {
        const created = Date.now();
        const update = {
          _id: `upd-${created}`,
          created,
          accountId: state.account.id,
          fromVersion: parsed?.fromVersion || "unknown",
          toVersion: parsed?.toVersion || parsed?.fromVersion || "unknown",
          changes: parsed?.changes || "",
          url: `https://api.jibo.com/update/upd-${created}`,
          shaHash: parsed?.shaHash || "fake-sha-hash",
          length: parsed?.length || 0,
          subsystem: parsed?.subsystem || "unknown",
          filter: parsed?.filter || null,
          dependencies: parsed?.dependencies || {}
        };

        state.updates.push(update);

        return {
          statusCode: 200,
          note: "Created update metadata",
          body: normalizeUpdate(update)
        };
      }

      if (operation === "RemoveUpdate") {
        const id = parsed?.id;
        const idx = state.updates.findIndex((u) => u._id === id);

        if (idx >= 0) {
          const [removed] = state.updates.splice(idx, 1);
          return {
            statusCode: 200,
            note: "Removed update",
            body: normalizeUpdate(removed)
          };
        }

        return {
          statusCode: 200,
          note: "Update not found; returned placeholder",
          body: {
            _id: id || "unknown-update",
            created: Date.now(),
            accountId: state.account.id,
            fromVersion: "unknown",
            toVersion: "unknown",
            changes: "Update not found",
            url: "https://api.jibo.com/update/missing",
            shaHash: "missing",
            length: 0,
            subsystem: "unknown",
            filter: null,
            dependencies: {}
          }
        };
      }

      return {
        statusCode: 200,
        note: "Default update handler",
        body: []
      };
    }

    function handleRobotOperation(operation, parsed) {
      if (operation === "UpdateRobot") {
        return {
          statusCode: 200,
          note: "Robot updated",
          body: {
            result: "ok"
          }
        };
      }

      if (operation === "GetRobot") {
        const id = parsed?.id || state.robot.id;
        return {
          statusCode: 200,
          note: "Returned robot",
          body: {
            id,
            payload: {
              SSID,
              connectedAt: Date.now(),
              platform: "12.10.0",
              serialNumber: "my-robot-serial-number"
            },
            calibrationPayload: {},
            updated: Date.now(),
            created: state.robotCreated ?? (state.robotCreated = Date.now())
          }
        };
      }

      return {
        statusCode: 200,
        note: "Default robot handler",
        body: { result: "ok" }
      };
    }

    function chooseResponse(record) {
      const { servicePrefix, operation } = record.target;
      const parsed = record.bodyJson || {};
      const host = record.host;
      
      if (record.method === "PUT" && record.url === "/upload/asr-binary") {
        logTiming("put_asr_binary_upload", {
          reqId: record.reqId,
          bodyLength: record.bodyLength,
          bodyHexPreview: record.bodyHexPreview
        });

        return {
          statusCode: 200,
          note: "Accepted uploaded blob",
          rawBody: ""
        };
      }

      if (
        record.method === "PUT" &&
        (
          record.url === "/upload/log-events" ||
          record.url === "/upload/log-binary"
        )
      ) {
        return {
          statusCode: 200,
          note: "Accepted uploaded blob",
          rawBody: ""
        };
      }

      if (record.method === "GET" && record.url === "/" && !record.target.raw) {
        return {
          statusCode: 204,
          note: "Root probe",
          body: {}
        };
      }

      if (record.method === "GET" && record.url === "/health") {
        return {
          statusCode: 200,
          note: "Health check",
          body: { ok: true, host }
        };
      }

      if (host !== "api.jibo.com") {
        console.log("NON-API HOST HIT:", host, record.method, record.url, record.target.raw || "<none>");
        writeStructuredLog("non_api_host_hit", {
          at: nowIso(),
          reqId: record.reqId,
          host,
          method: record.method,
          url: record.url,
          target: record.target.raw || "",
          headers: record.headers,
          bodyLength: record.bodyLength,
          bodyUtf8: record.bodyLooksText ? record.bodyUtf8 : "",
          bodyHexPreview: record.bodyHexPreview
        });

        return {
          statusCode: 200,
          note: "Default catch-all non-api host handler",
          body: { ok: true, host, note: "default catch-all handler" }
        };
      }

      if (servicePrefix.startsWith("Log_")) {
        return handleLogOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Backup_")) {
        return handleBackupOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Account_")) {
        return handleAccountOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Notification_")) {
        return handleNotificationOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Loop_")) {
        return handleLoopOperation(operation, parsed);
      }

      if (servicePrefix === "Media_20160725") {
        return handleMediaOperation(operation, parsed, record);
      }

      if (servicePrefix.startsWith("Key_")) {
        return handleKeyOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Person_")) {
        return handlePersonOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Robot_")) {
        return handleRobotOperation(operation, parsed);
      }

      if (servicePrefix.startsWith("Update_")) {
        return handleUpdateOperation(operation, parsed);
      }

      writeStructuredLog("unknown_target", {
        at: nowIso(),
        reqId: record.reqId,
        host,
        method: record.method,
        url: record.url,
        target: record.target.raw,
        servicePrefix,
        operation,
        headers: record.headers,
        bodyLength: record.bodyLength,
        bodyUtf8: record.bodyLooksText ? record.bodyUtf8 : "",
        bodyHexPreview: record.bodyHexPreview,
        bodyJson: parsed
      });

      return {
        statusCode: 200,
        note: "Unknown target; logged for follow-up",
        body: {
          ok: true,
          host,
          target: record.target.raw,
          operation,
          note: "unknown target default response"
        }
      };
    }

    function buildWsContext(request) {
      const context = classifyWebSocket(request);
      return {
        ...context,
        wsId: `ws-${Date.now()}-${crypto.randomBytes(3).toString("hex")}`
      };
    }

    function logWsConnected(ctx, request) {
      console.log("==== WS CONNECTED ====");
      console.log("WsId:", ctx.wsId);
      console.log("Time:", nowIso());
      console.log("Host:", ctx.host);
      console.log("Path:", ctx.url);
      console.log("WS KIND:", ctx.kind);

      if (ctx.kind === "api-socket") {
        console.log("Token known:", ctx.pathTokenKnown);
      } else {
        console.log("Bearer Token:", ctx.bearerToken);
        console.log("Bearer Token Known:", ctx.bearerTokenKnown);
        console.log("TransId:", ctx.transId);
        console.log("RobotId:", ctx.robotId);
      }

      console.log("Headers:", JSON.stringify(request.headers, null, 2));
      console.log("======================");

      writeStructuredLog("ws_connected", {
        at: nowIso(),
        wsId: ctx.wsId,
        host: ctx.host,
        path: ctx.url,
        kind: ctx.kind,
        pathTokenKnown: ctx.pathTokenKnown,
        bearerTokenKnown: ctx.bearerTokenKnown,
        transId: ctx.transId,
        robotId: ctx.robotId,
        headers: request.headers
      });
    }

    function sendApiSocketGreeting(ws, ctx) {
      if (ws.readyState !== WebSocket.OPEN) return;
      
      // Intentionally do nothing.
      // The robot opens api-socket successfully without needing our fake greeting,
      // and the previous fake message caused NotificationManager to dereference
      // message.payload.name and throw.
      return;
      
      // if we need to send, send below

      ws.send(JSON.stringify({
        "payload": {
          "name": "StatusConnected",
          "payload": {}
        }
      }));
    }

    function getOrCreateHubSession(ctx) {
      const key = buildHubSessionKey(ctx);
      let session = state.hubSessions.get(key);

      if (!session) {
        session = {
          key,
          createdAt: nowIso(),
          responded: false,
          closed: false,
          sawListen: false,
          sawContext: false,
          listenMsg: null,
          contextMsg: null,
          audioBytes: 0,
          audioFrames: 0,
          lastAudioAt: null,
          lastListenAt: null,
          lastContextAt: null,
          clientNluMsg: null,
          lastClientNluAt: null,
          
          audioChunks: [],
          audioFilePath: null,
          wavFilePath: null,
          wavFilePathOriginal: null,
          asrTimer: null,
          asrRunning: false,
          asrDone: false,
          lastTranscript: null,
          firstAudioAtMs: null,
          lastAudioAtMs: null,
          partialResults: [],
          bestTranscript: "",
          lastNonEmptyTranscript: "",
          successfulChunkCount: 0,
          processedChunkCount: 0,
          finalizing: false,
          finalizeStartedAtMs: null,
          finalizeDeadlineAtMs: null,
          finalizeTimer: null,
          lastFinalizeAttemptAtMs: null,
          turnStartedAt: Date.now()
        };

        state.hubSessions.set(key, session);
        pruneHubSessions();
      }

      return session;
    }

    function makeSessionFileBase(ctx, session) {
      const transId = (ctx.transId || "no-trans").replace(/[^a-zA-Z0-9._-]/g, "_");
      return path.join(ASR_TMP_DIR, `${Date.now()}_${transId}`);
    }

    function cleanupSessionAudioFiles(session) {
      if (!session) return;

      for (const f of [session.audioFilePath, session.wavFilePath, session.wavFilePathOriginal]) {
        if (!f) continue;
        try { fs.unlinkSync(f); } catch {}
      }

      session.audioFilePath = null;
      session.wavFilePath = null;
      session.wavFilePathOriginal = null;
    }

    function runProcess(command, args, options = {}) {
      return new Promise((resolve, reject) => {
        const child = spawn(command, args, {
          stdio: ["ignore", "pipe", "pipe"],
          ...options
        });

        let stdout = "";
        let stderr = "";

        child.stdout.on("data", (d) => { stdout += d.toString("utf8"); });
        child.stderr.on("data", (d) => { stderr += d.toString("utf8"); });
        child.on("error", reject);

        child.on("close", (code) => {
          if (code === 0) {
            resolve({ stdout, stderr, code });
          } else {
            reject(new Error(`${command} exited with code ${code}\n${stderr}`));
          }
        });
      });
    }
    
    async function trimAndBoostWav(inputWavPath, outputWavPath) {
      await runProcess(FFMPEG_BIN, [
        "-y",
        "-i", inputWavPath,
        "-af",
        "silenceremove=start_periods=1:start_duration=0.08:start_threshold=-42dB:stop_periods=-1:stop_duration=0.60:stop_threshold=-42dB,apad=pad_dur=0.6,volume=6dB",
        "-ar", "16000",
        "-ac", "1",
        "-f", "wav",
        outputWavPath
      ]);
    }
    
    async function analyzeWav(wavPath) {
      return await runProcess(FFMPEG_BIN, [
        "-i", wavPath,
        "-af", "volumedetect",
        "-f", "null",
        "-"
      ]);
    }

    async function convertOggToWav(oggPath, wavPath) {
      await runProcess(FFMPEG_BIN, [
        "-y",
        "-i", oggPath,
        "-ar", "16000",
        "-ac", "1",
        "-f", "wav",
        wavPath
      ]);
    }

    async function transcribeWithWhisperCpp(wavPath) {
      const args = [
        "-m", WHISPER_MODEL,
        "-f", wavPath,
        "-l", "en"
      ];

      const result = await runProcess(WHISPER_CPP_BIN, args);

      logTiming("whisper_cli_result", {
        wavPath,
        stdout: result.stdout,
        stderr: result.stderr
      });

      const lines = result.stdout
        .split(/\r?\n/)
        .map((s) => s.trim())
        .filter(Boolean);

      const transcriptLines = lines
        .filter((line) => /^\[\d{2}:\d{2}:\d{2}\.\d{3}\s+-->/.test(line))
        .map((line) => line.replace(/^\[[^\]]+\]\s*/, "").trim())
        .filter(Boolean);

      return transcriptLines.join(" ").trim();
    }

    function classifyTranscript(text) {
      const heard = String(text || "").trim();
      const lower = normalizeTranscriptText(text);
      
      console.log("LOOKING FOR NORMALIZED TEXT", JSON.stringify({
        lower
      }, null, 2));
      
      if (!heard) {
        return { intent: "unknown", replyType: "fallback", heardText: "" };
      }

      if (/\bjoke\b|funny|make me laugh/.test(lower)) {
        return { intent: "joke", replyType: "joke", heardText: heard };
      }
      
      if (/\bdate and time\b/.test(lower)) {
        return { intent: "date_time", replyType: "chat", heardText: heard };
      }
      
      if (lower === "time") {
        return { intent: "time", replyType: "chat", heardText: heard };
      }

      if (/\bwhat time is it\b|\bcurrent time\b|\btime is it\b|\bthe time\b/.test(lower)) {
        return { intent: "time", replyType: "chat", heardText: heard };
      }
      
      if (lower === "today" ||lower === "day" || lower === "date") {
        return { intent: "date", replyType: "chat", heardText: heard };
      }

      if (/\bwhat day is it\b|\bwhat is the date\b|\btoday'?s date\b|\bdate\b|\bwhat day\b/.test(lower)) {
        return { intent: "date", replyType: "chat", heardText: heard };
      }

      if (/\bdance\b/.test(lower)) {
        return { intent: "dance", replyType: "chat", heardText: heard };
      }

      if (/\bhow are you\b|\bwhats up\b|\bwhat s up\b|\bwhat up\b/.test(lower)) {
        return { intent: "how_are_you", replyType: "chat", heardText: heard };
      }
      
      if (/\bgood morning\b/.test(lower)) {
        return { intent: "good_morning", replyType: "chat", heardText: heard };
      }
      
      if (/\bgood afternoon\b/.test(lower)) {
        return { intent: "good_afternoon", replyType: "chat", heardText: heard };
      }
      
      if (/\bgood night\b|\bnight night\b|\bbed time\b/.test(lower)) {
        return { intent: "good_night", replyType: "chat", heardText: heard };
      }
      
      if (/\bhello\b|\bhi\b|\bhey\b/.test(lower)) {
        return { intent: "hello", replyType: "chat", heardText: heard };
      }
      
      if (/\byes\b|\bsure\b|\byeah\b|\byup\b|\buh huh\b/.test(lower)) {
        return { intent: "yes", replyType: "chat", heardText: heard };
      }
      
      if (/\bno\b|\bnope\b|\bnah\b/.test(lower)) {
        return { intent: "no", replyType: "chat", heardText: heard };
      }
      
      return { intent: "chat", replyType: "chat", heardText: heard };
    }
    
    function isTranscriptUsable(text, session = null) {
      const t = String(text || "")
        .trim()
        .toLowerCase()
        .replace(/[^\w\s]/g, "")
        .replace(/\s+/g, " ")
        .trim();

      if (!t) return false;

      if ([
        "joke",
        "dance",
        "time",
        "date",
        "good morning",
        "good afternoon",
        "good night",
        "hello",
        "what's up?",
        "what up",
        "how are you",
        "hi",
        "hey"
      ].includes(t)) {
        return true;
      }
      
      if (isYesNoTurn(session) && ["yes", "no", "sure", "nope", "yup", "uh huh", "yeah", "nah"].includes(t)) {
        return true;
      }
      
      return t.length >= 6;
    }
    
    function normalizeTranscriptText(text) {
      return String(text || "")
        .trim()
        .toLowerCase()
        .replace(/[^\w\s]/g, " ")
        .replace(/\s+/g, " ")
        .trim();
    }
    
    function getLocalDateTimeParts() {
      	const now = new Date();

      const timeFormatter = new Intl.DateTimeFormat("en-US", {
        timeZone: "America/Chicago",
        hour: "numeric",
        minute: "2-digit",
        hour12: true
      });

      const dateFormatter = new Intl.DateTimeFormat("en-US", {
        timeZone: "America/Chicago",
        weekday: "long",
        year: "numeric",
        month: "long",
        day: "numeric"
      });

      return {
        now,
        timeText: timeFormatter.format(now),
        dateText: dateFormatter.format(now)
      };
    }

    function buildTimeResponseText() {
      const { timeText } = getLocalDateTimeParts();
      return `It is ${timeText}.`;
    }

    function buildDateResponseText() {
      const { dateText } = getLocalDateTimeParts();
      return `Today is ${dateText}.`;
    }
    
    function pickMoodForIntent(intent, heardText) {
      if (intent === "joke") return "playful";
      if (intent === "good_morning") return "excited";
      if (intent === "good_afternoon") return "happy";
      if (intent === "good_night") return "warm";
      if (intent === "hello") return "warm";
      if (intent === "how_are_you") return "happy";
      if (intent === "dance") return "excited";
      if (intent === "date_time") return "helpful";
      if (intent === "time") return "helpful";
      if (intent === "date") return "helpful";
      if (intent === "yes") return "yes";
      if (intent === "no") return "no";
      if (!heardText) return "confused";
      return "curious";
    }

    function pickEsCategoryForMood(mood) {
      switch (mood) {
        case "playful":
          return "happy";
        case "warm":
          return "happy";
        case "happy":
          return "happy";
        case "helpful":
          return "curious";
        case "excited":
          return "excited";
        case "confused":
          return "confused";
        case "curious":
        default:
          return "curious";
      }
    }
    
    const DANCES = [
      "rom-upbeat",
      "rom-ballroom",
      "rom-silly",
      "rom-slowdance",
      "rom-electronic",
      "rom-twerk"
    ];
    
    function buildGenericChatEsml(heardText, intent) {
      const safe = escapeXml(heardText);
      const mood = pickMoodForIntent(intent, heardText);
      const cat = pickEsCategoryForMood(mood);

      let text;

      if (intent === "hello") {
        text = "Hi there. It is really good to talk with you.";
      } else if (intent === "good_morning") {
        text = "Good morning! It's a beautiful day!";
      } else if (intent === "good_afternoon") {
        text = "Good afternoon back at you!";
      } else if (intent === "good_night") {
        text = "Good night. Sleep tight.";
      } else if (intent === "how_are_you") {
        text = "I am feeling cheerful and robotic.";
      } else if (intent === "date_time") {
        text = `${buildDateResponseText()} ${buildTimeResponseText()}`;
      } else if (intent === "time") {
        text = buildTimeResponseText();
      } else if (intent === "date") {
        text = buildDateResponseText();
      } else if (intent === "dance") {
        const dance = randomItem(DANCES);
        return `<speak>Okay.<break size='0.2'/> Watch this.<anim cat='dance' filter='music, ${dance}' /></speak>`;
      } else if (intent === "yes") {
        text = "Yes.";
      } else if (intent === "no") {
        text = "No.";
      } else if (!heardText || heardText.includes("blank audio")) {
        text = "Hmm. I heard you, but I did not catch that.";
      } else {
        text = `Okay. You said, ${safe}.`;
      }

      return `<speak><es cat='${cat}' filter='!ssa-only, !sfx-only' endNeutral='true'>${text.replace(/\.\s+/g, ".<break size='0.2'/> ")}</es></speak>`;
    }
    
    function sendGenericChatSkill(ws, ctx, session, heardText, intent) {
      const transID = session.listenMsg?.transID || ctx.transId || "";
      const esml = buildGenericChatEsml(heardText, intent);

      return sendWsJson(ws, {
        type: "SKILL_ACTION",
        ts: Date.now(),
        msgID: makeHubMsgId(),
        transID,
        data: {
          skill: { id: "chitchat-skill" },
          action: {
            config: {
              jcp: {
                type: "SLIM",
                config: {
                  play: {
                    esml,
                    meta: {
                      prompt_id: "RUNTIME_PROMPT",
                      prompt_sub_category: "AN",
                      mim_id: "runtime-chat",
                      mim_type: "announcement"
                    }
                  }
                }
              }
            }
          },
          analytics: {},
          final: true
        }
      }, "ws_out_generic_chat");
    }
    
    function completeTurnWithRoute(ws, ctx, session, route) {
      const listenData = session.listenMsg?.data || {};
      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];

      sendSyntheticListenResult(ws, ctx, session, rules, route.intent, route.heardText);
      sendSyntheticEos(ws, ctx, session);

      const isYesNoIntent = route.intent === "yes" || route.intent === "no";
      const yesNoSession = isYesNoTurn(session);
      
      console.log("CompleteTurnWithRoute", JSON.stringify({
          listenData,
          rules,
          isYesNoIntent,
          yesNoSession
        }, null, 2));
      
      if (!isYesNoIntent && !yesNoSession) {
        setTimeout(() => {
          if (route.replyType === "joke") {
            const joke = randomItem(JOKES);
            sendWsJson(
              ws,
              buildJokeSkillActionPayload(
                session.listenMsg?.transID || ctx.transId || "",
                joke
              ),
              "ws_out_joke_skill"
            );
          } else {
            sendGenericChatSkill(ws, ctx, session, route.heardText, route.intent);
          }
        }, 75);
      }

      session.responded = true;
      session.respondedAt = nowIso();
      session.asrDone = true;
    }

    function completeTurnFallback(ws, ctx, session, reason = "fallback") {
      const listenData = session.listenMsg?.data || {};
      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];

      logTiming("ws_fallback_result", {
        transID: session.listenMsg?.transID || ctx.transId || "",
        reason
      });

      sendSyntheticListenResult(ws, ctx, session, rules, "heyJibo", "");
      sendSyntheticEos(ws, ctx, session);

      setTimeout(() => {
        sendGenericChatSkill(ws, ctx, session, "", "unknown");
      }, 75);

      session.responded = true;
      session.respondedAt = nowIso();
      session.respondedReason = reason;
      session.asrDone = true;
    }
    
    const OGG_CRC_TABLE = (() => {
      const table = new Uint32Array(256);
      for (let i = 0; i < 256; i++) {
        let r = i << 24;
        for (let j = 0; j < 8; j++) {
          r = (r & 0x80000000) ? ((r << 1) ^ 0x04c11db7) >>> 0 : (r << 1) >>> 0;
        }
        table[i] = r >>> 0;
      }
      return table;
    })();

    function computeOggCrc(buf) {
      let crc = 0;
      for (let i = 0; i < buf.length; i++) {
        crc = ((crc << 8) ^ OGG_CRC_TABLE[((crc >>> 24) ^ buf[i]) & 0xff]) >>> 0;
      }
      return crc >>> 0;
    }
    
    function normalizeOggPages(chunks) {
      const parsed = chunks.map((chunk, i) => {
        const p = parseOggPage(chunk);
        if (!p.ok) {
          throw new Error(`Invalid Ogg page at index ${i}: ${p.error || "unknown"}`);
        }
        return { index: i, chunk, page: p };
      });

      if (parsed.length === 0) return Buffer.alloc(0);

      const baseGranule = BigInt(parsed[1]?.page?.granulePos || parsed[0].page.granulePos || "0");

      const rewritten = parsed.map(({ index, chunk, page }) => {
        const out = Buffer.from(chunk);

        // Normalize granule positions to start near zero after headers.
        let newGranule = 0n;
        if (index >= 1) {
          const g = BigInt(page.granulePos);
          newGranule = g >= baseGranule ? (g - baseGranule) : 0n;
        }

        out.writeBigUInt64LE(newGranule, 6);

        // Normalize sequence numbers just in case.
        out.writeUInt32LE(index >>> 0, 18);

        // Set EOS on final page only.
        let headerType = out.readUInt8(5);
        headerType = index === parsed.length - 1 ? (headerType | 0x04) : (headerType & ~0x04);
        out.writeUInt8(headerType, 5);

        // Zero checksum before recomputing.
        out.writeUInt32LE(0, 22);
        const crc = computeOggCrc(out);
        out.writeUInt32LE(crc >>> 0, 22);

        return out;
      });

      return Buffer.concat(rewritten);
    }
    
    function parseOggPage(buf) {
      if (!Buffer.isBuffer(buf) || buf.length < 27) {
        return { ok: false, error: "too_short", length: buf ? buf.length : 0 };
      }

      const capture = buf.toString("ascii", 0, 4);
      if (capture !== "OggS") {
        return { ok: false, error: "bad_capture", capture, length: buf.length };
      }

      const version = buf.readUInt8(4);
      const headerType = buf.readUInt8(5);
      const granulePos = buf.readBigUInt64LE(6);
      const bitstreamSerial = buf.readUInt32LE(14);
      const pageSequenceNo = buf.readUInt32LE(18);
      const checksum = buf.readUInt32LE(22);
      const pageSegments = buf.readUInt8(26);

      if (buf.length < 27 + pageSegments) {
        return {
          ok: false,
          error: "short_segment_table",
          length: buf.length,
          pageSegments
        };
      }

      let payloadLen = 0;
      const lacing = [];
      for (let i = 0; i < pageSegments; i++) {
        const seg = buf.readUInt8(27 + i);
        lacing.push(seg);
        payloadLen += seg;
      }

      const headerLen = 27 + pageSegments;
      const expectedTotalLen = headerLen + payloadLen;

      return {
        ok: true,
        capture,
        version,
        headerType,
        continued: !!(headerType & 0x01),
        bos: !!(headerType & 0x02),
        eos: !!(headerType & 0x04),
        granulePos: granulePos.toString(),
        bitstreamSerial,
        pageSequenceNo,
        checksum,
        pageSegments,
        headerLen,
        payloadLen,
        expectedTotalLen,
        actualLen: buf.length,
        lacingPreview: lacing.slice(0, 12)
      };
    }

    function beginFinalizeTurn(ws, ctx, session, reason = "max-turn") {
      if (!ASR_ENABLED) return false;
      if (session.responded || session.asrDone) return false;
      if (!session.sawListen || !session.sawContext) return false;
      if (session.audioBytes < ASR_MIN_AUDIO_BYTES) return false;
      if (session.audioFrames < ASR_MIN_AUDIO_FRAMES) return false;
      if (ws.readyState !== WebSocket.OPEN) return false;

      if (!session.finalizing) {
        session.finalizing = true;
        session.finalizeStartedAtMs = Date.now();
        session.finalizeDeadlineAtMs = session.finalizeStartedAtMs + ASR_FINALIZE_GRACE_MS;

        logTiming("ws_begin_finalize", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          reason,
          audioFrames: session.audioFrames,
          audioBytes: session.audioBytes,
          finalizeGraceMs: ASR_FINALIZE_GRACE_MS
        });
      }

      scheduleFinalizeAttempt(ws, ctx, session);
      
      if (!session.asrRunning) {
        tryFinalizeTurn(ws, ctx, session, "begin").catch((err) => {
          console.error("tryFinalizeTurn begin error:", err);
        });
      }
      
      return true;
    }
    
    function isYesNoTurn(session) {
      const listenData = session?.listenMsg?.data || {};
      const asr = listenData.asr || {};
      const hints = Array.isArray(asr.hints) ? asr.hints : [];
      const earlyEOS = Array.isArray(asr.earlyEOS) ? asr.earlyEOS : [];
      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];

      return (
        hints.includes("$YESNO") ||
        earlyEOS.includes("$YESNO") ||
        rules.includes("create/is_it_a_keeper")
      );
    }
    
    async function tryFinalizeTurn(ws, ctx, session, reason = "poll") {
      if (!session.finalizing) return false;
      if (session.responded || session.asrDone || session.asrRunning) return false;
      if (ws.readyState !== WebSocket.OPEN) return false;

      session.asrRunning = true;
      session.lastFinalizeAttemptAtMs = Date.now();

      try {
        const base = makeSessionFileBase(ctx, session);
        const oggPath = `${base}.ogg`;
        const wavPath = `${base}.wav`;
        const trimmedWavPath = `${base}.trimmed.wav`;

        session.audioFilePath = oggPath;
        session.wavFilePathOriginal = wavPath;
        session.wavFilePath = trimmedWavPath;

        const pageSummaries = session.audioChunks.map((chunk, i) => {
          const p = parseOggPage(chunk);
          return {
            i,
            ok: p.ok,
            len: chunk.length,
            serial: p.bitstreamSerial,
            seq: p.pageSequenceNo,
            granule: p.granulePos,
            bos: p.bos,
            eos: p.eos,
            continued: p.continued,
            expectedTotalLen: p.expectedTotalLen,
            actualLen: p.actualLen
          };
        });

        if (ASR_DEBUG_OGG) {
          writeStructuredLog("ogg_page_summary", {
            at: nowIso(),
            transID: session.listenMsg?.transID || ctx.transId || "",
            pageSummaries
          });
        }

        const normalizedOgg = normalizeOggPages(session.audioChunks);
        fs.writeFileSync(oggPath, normalizedOgg);

        logTiming("ws_normalized_ogg_written", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          normalizedSize: normalizedOgg.length
        });

        const oggSize = fs.statSync(oggPath).size;
        logTiming("ws_finalize_attempt", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          reason,
          audioFrames: session.audioFrames,
          audioBytes: session.audioBytes,
          oggSize
        });

        await convertOggToWav(oggPath, wavPath);
        let wavSize = fs.statSync(wavPath).size;

        logTiming("ws_finalize_wav", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          wavSize
        });

        await trimAndBoostWav(wavPath, trimmedWavPath);

        const trimmedWavSize = fs.statSync(trimmedWavPath).size;
        logTiming("ws_finalize_wav_trimmed", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          wavSize: trimmedWavSize
        });

        if (ASR_DEBUG_AUDIO) {
          const analysis = await analyzeWav(trimmedWavPath);
          logTiming("ws_finalize_audio_analysis", {
            transID: session.listenMsg?.transID || ctx.transId || "",
            stderr: analysis.stderr
          });
        }

        if (trimmedWavSize >= ASR_MIN_GOOD_WAV_BYTES) {
          const transcript = (await transcribeWithWhisperCpp(trimmedWavPath)).trim();

          logTiming("ws_finalize_transcript", {
            transID: session.listenMsg?.transID || ctx.transId || "",
            transcript,
            source: "trimmed"
          });

          if (isTranscriptUsable(transcript, session)) {
            session.lastTranscript = transcript;
            const route = classifyTranscript(transcript);
            completeTurnWithRoute(ws, ctx, session, route);
            return true;
          }
        }

        if (wavSize >= ASR_MIN_GOOD_WAV_BYTES) {
          const transcript = (await transcribeWithWhisperCpp(wavPath)).trim();

          logTiming("ws_finalize_transcript", {
            transID: session.listenMsg?.transID || ctx.transId || "",
            transcript,
            source: "original"
          });

          if (isTranscriptUsable(transcript, session)) {
            session.lastTranscript = transcript;
            const route = classifyTranscript(transcript);
            completeTurnWithRoute(ws, ctx, session, route);
            return true;
          }
        }

        if (Date.now() >= session.finalizeDeadlineAtMs) {
          completeTurnFallback(ws, ctx, session, "finalize-timeout");
          return true;
        }
    
        scheduleFinalizeAttempt(ws, ctx, session);
        return false;
      } catch (err) {
        logTiming("ws_finalize_error", {
          transID: session.listenMsg?.transID || ctx.transId || "",
          error: err?.message
        });
    
        if (Date.now() >= session.finalizeDeadlineAtMs) {
          completeTurnFallback(ws, ctx, session, "finalize-error-timeout");
          return true;
        }
    
        scheduleFinalizeAttempt(ws, ctx, session);
        return false;
      } finally {
        session.asrRunning = false;
        cleanupSessionAudioFiles(session);
      }
    }

    function scheduleFinalizeAttempt(ws, ctx, session) {
      if (!session.finalizing || session.responded || session.asrDone) return;

      if (session.finalizeTimer) {
        clearTimeout(session.finalizeTimer);
        session.finalizeTimer = null;
      }

      session.finalizeTimer = setTimeout(() => {
        session.finalizeTimer = null;
        tryFinalizeTurn(ws, ctx, session, "timer").catch((err) => {
          console.error("tryFinalizeTurn uncaught error:", err);
        });
      }, ASR_FINALIZE_POLL_MS);
    }
    
    function makeHubMsgId() {
      return `mid-${crypto.randomUUID()}`;
    }

    function buildEosPayload(transID) {
      return {
        type: "EOS",
        ts: Date.now(),
        msgID: makeHubMsgId(),
        transID,
        data: {}
      };
    }
    
    function getYesNoCreateRule(session) {
      const listenData = session?.listenMsg?.data || {};
      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];

      if (rules.includes("create/is_it_a_keeper")) {
        return "create/is_it_a_keeper";
      }

      return null;
    }

    function buildSyntheticListenPayload(ctx, session, rules, intent, heardText) {
      const transID = session?.listenMsg?.transID || ctx.transId || "";
      const normalizedText = String(heardText || "").trim();
      const yesNoCreateRule = getYesNoCreateRule(session);
      const isYesNo = intent === "yes" || intent === "no";
      
      console.log("LOOKING FOR INTENT CHANGE", JSON.stringify({
          intent,
          heardText,
          normalizedText,
          isYesNo,
          rule: yesNoCreateRule,
          entities: { domain: "create" }
        }, null, 2));

      // HACK TEST:
      // If this is a create-flow yes/no turn, make the result skill-local instead of global-looking.
      if (isYesNo || yesNoCreateRule) {
        console.log("YESNO CREATE HACK PAYLOAD", JSON.stringify({
          intent,
          heardText: normalizedText,
          rule: yesNoCreateRule,
          entities: { domain: "create" }
        }, null, 2));
        return {
          type: "LISTEN",
          transID,
          data: {
            asr: {
              confidence: 0.95,
              final: true,
              text: normalizedText || (intent === "yes" ? "- Yes." : "- No.")
            },
            nlu: {
              confidence: 0.95,
              intent,
              rules: [yesNoCreateRule],
              entities: {
                domain: "create"
              }
            },
            match: {
              intent,
              rule: yesNoCreateRule,
              score: 0.95
            }
          }
        };
      }

      console.log("GENERIC SYNTHETIC LISTEN PAYLOAD", JSON.stringify({
        intent,
        normalizedText,
        rules: Array.isArray(rules) ? rules : []
      }, null, 2));
      
      // default/original behavior
      return {
        type: "LISTEN",
        transID,
        data: {
          asr: {
            confidence: 0.95,
            final: true,
            text: normalizedText
          },
          nlu: {
            confidence: 0.95,
            intent,
            rules: Array.isArray(rules) ? rules : [],
            entities: {}
          },
          match: {
            intent,
            rule: Array.isArray(rules) && rules.length ? rules[0] : "",
            score: 0.95
          }
        }
      };
    }

    function buildJokeSkillActionPayload(transID, jokeText) {
      return {
        type: "SKILL_ACTION",
        ts: Date.now(),
        msgID: makeHubMsgId(),
        transID,
        data: {
          skill: { id: "@be/joke" },
          action: {
            config: {
              jcp: {
                type: "SLIM",
                config: {
                  play: {
                    esml: `<speak><es cat='happy' filter='!ssa-only, !sfx-only' endNeutral='true'>${escapeXml(jokeText)}</es></speak>`,
                    meta: {
                      prompt_id: "RUNTIME_PROMPT",
                      prompt_sub_category: "AN",
                      mim_id: "runtime-joke",
                      mim_type: "announcement"
                    }
                  }
                }
              }
            }
          },
          analytics: {},
          final: true
        }
      };
    }

    function sendSyntheticListenResult(ws, ctx, session, rules, intent, asrText = "") {
      const transID = session.listenMsg?.transID || ctx.transId || "";
      
      logTiming("ws_send_listen_result", {
        transID,
        intent,
        asrText
      });
      
      return sendWsJson(
        ws,
        buildSyntheticListenPayload(ctx, session, rules, intent, asrText),
        "ws_out_listen"
      );
    }
    
    function sendClientAsrJokeFlow(ws, ctx, session, parsed) {
      const transID = session.listenMsg?.transID || ctx.transId || "";
      const listenData = session.listenMsg?.data || {};
      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];
      const heardText = parsed?.data?.text || "tell me a joke";
      const joke = randomItem(JOKES);

      console.log("CLIENT_ASR joke flow");
      console.log("Heard text:", heardText);
      console.log("Selected joke:", joke);

      sendSyntheticListenResult(ws, ctx, session, rules, "joke", heardText);
      sendSyntheticEos(ws, ctx, session);

      setTimeout(() => {
        sendWsJson(
          ws,
          buildJokeSkillActionPayload(transID, joke),
          "ws_out_joke_skill"
        );
      }, 75);

      session.responded = true;
      session.respondedAt = nowIso();
      session.respondedReason = "client-asr-joke";

      writeStructuredLog("ws_synthetic_client_asr_turn_complete", {
        at: nowIso(),
        wsId: ctx.wsId,
        transID,
        heardText,
        selectedJoke: joke,
        rules,
        audioFrames: session.audioFrames,
        audioBytes: session.audioBytes
      });

      return true;
    }

    function maybeEmitSyntheticHubListen(ws, ctx, session, reason) {
      if (!isNeoHubListen(ctx)) return false;
      if (session.responded) return false;
      if (!session.sawListen) return false;
      if (!session.sawContext) return false;
      if (ws.readyState !== WebSocket.OPEN) return false;

      const listenData = session.listenMsg?.data || {};
      const transID = session.listenMsg?.transID || ctx.transId || "";

      const isHotphrase = !!listenData.hotphrase;
      const hasClientNlu = !!session.clientNluMsg;
      const hasEnoughAudio = session.audioFrames >= 6 && session.audioBytes >= 6000;

      if (isHotphrase && !hasClientNlu && !hasEnoughAudio) {
        return false;
      }

      if (!hasClientNlu && !hasEnoughAudio) return false;
      
      console.log("ASR trigger reason", {
        reason,
        audioFrames: session.audioFrames,
        audioBytes: session.audioBytes,
        turnAgeMs: session.firstAudioAtMs ? Date.now() - session.firstAudioAtMs : null
      });

      const rules = Array.isArray(listenData.rules) ? listenData.rules : [];
      let intent = "heyJibo";
      let asrText = "";

      if (hasClientNlu) {
        intent = session.clientNluMsg.data.intent || "unknown";
        asrText = intent;

        console.log("CLIENT_NLU path → sending full response");

        sendSyntheticListenResult(ws, ctx, session, rules, intent, asrText);
        sendSyntheticEos(ws, ctx, session);

        setTimeout(() => {
          // I used to make this call here but I don't think we get here anymore and this function is gone
          // sendSyntheticNimbusSkill(ws, ctx, session);
        }, 75);

        session.responded = true;
        session.respondedAt = nowIso();
        session.respondedReason = reason;
        return true;
      } 
      
      // raw audio should now be handled by external ASR path, not here
      return false;
    }

    function handleNeoHubJsonMessage(ws, ctx, parsed) {
      if (!isNeoHubListen(ctx)) {
        writeStructuredLog("neo_hub_other_json_in", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          parsed
        });
        return;
      }

      const session = getOrCreateHubSession(ctx);

      if (parsed?.type === "LISTEN") {
        session.sawListen = true;
        session.listenMsg = parsed;
        session.lastListenAt = nowIso();

        writeStructuredLog("neo_hub_listen_in", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          parsed
        });

        maybeEmitSyntheticHubListen(ws, ctx, session, "listen-after-check");
        return;
      }

      if (parsed?.type === "CLIENT_NLU") {
        session.clientNluMsg = parsed;
        session.lastClientNluAt = nowIso();

        writeStructuredLog("neo_hub_client_nlu_in", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          parsed
        });

        maybeEmitSyntheticHubListen(ws, ctx, session, "client-nlu-after-check");
        return;
      }

      if (parsed?.type === "CONTEXT") {
        session.sawContext = true;
        session.contextMsg = parsed;
        session.lastContextAt = nowIso();

        writeStructuredLog("neo_hub_context_in", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          parsed
        });

        return;
      }

      const clientAsrText = String(parsed?.data?.text || "").trim().toLowerCase();
      if (parsed?.type === "CLIENT_ASR" && clientAsrText === "tell me a joke") {
        session.sawContext = true;
        session.contextMsg = parsed;
        session.lastContextAt = nowIso();

        writeStructuredLog("neo_hub_client_asr_in", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          parsed
        });

        sendClientAsrJokeFlow(ws, ctx, session, parsed);
        return;
      }

      writeStructuredLog("neo_hub_other_json_in", {
        at: nowIso(),
        wsId: ctx.wsId,
        host: ctx.host,
        path: ctx.url,
        kind: ctx.kind,
        parsed
      });
    }

    function sendSyntheticEos(ws, ctx, session) {
      const transID = session.listenMsg?.transID || ctx.transId || "";
      
      logTiming("ws_send_eos", {
        transID
      });
      
      return sendWsJson(
        ws,
        buildEosPayload(transID),
        "ws_out_eos"
      );
    }

    function handleNeoHubBinaryMessage(ws, ctx, data) {
      if (!isNeoHubListen(ctx)) return;

      const session = getOrCreateHubSession(ctx);

      const now = Date.now();
      if (!session.firstAudioAtMs) session.firstAudioAtMs = now;
      session.lastAudioAtMs = now;

      session.audioFrames += 1;
      session.audioBytes += data.length;
      session.lastAudioAt = nowIso();
      session.audioChunks.push(Buffer.from(data));
      
      const page = parseOggPage(data);

      logTiming("ogg_page_in", {
        transID: session.listenMsg?.transID || ctx.transId || "",
        chunkIndex: session.audioFrames - 1,
        length: data.length,
        ...page
      });

      const turnAge = now - session.firstAudioAtMs;

      const canStartEarly =
        turnAge >= ASR_EARLY_START_MS &&
        session.audioFrames >= ASR_EARLY_MIN_FRAMES &&
        session.audioBytes >= ASR_EARLY_MIN_BYTES;

      const hitHardTurnLimit =
        turnAge >= ASR_MAX_TURN_MS &&
        session.audioFrames >= ASR_MIN_AUDIO_FRAMES &&
        session.audioBytes >= ASR_MIN_AUDIO_BYTES;

      if (!session.responded && !session.asrDone && (canStartEarly || hitHardTurnLimit)) {
        beginFinalizeTurn(ws, ctx, session, canStartEarly ? "early-start" : "max-turn");
        return;
      }

      if (session.finalizing) {
        scheduleFinalizeAttempt(ws, ctx, session);
      }
    }

    function normalizeWsData(data) {
      if (Buffer.isBuffer(data)) return data;
      if (Array.isArray(data)) {
        return Buffer.concat(
          data.map((item) => (Buffer.isBuffer(item) ? item : Buffer.from(item)))
        );
      }
      if (data instanceof ArrayBuffer) return Buffer.from(data);
      if (ArrayBuffer.isView(data)) {
        return Buffer.from(data.buffer, data.byteOffset, data.byteLength);
      }
      return Buffer.from(String(data || ""), "utf8");
    }

    function isNeoHubListen(ctx) {
      return ctx.kind === "neo-hub-listen";
    }

    function buildHubSessionKey(ctx) {
      return `${ctx.host}|${ctx.url}|${ctx.transId || "no-trans"}|${ctx.robotId || "no-robot"}`;
    }

    function findHubSession(ctx) {
      return state.hubSessions.get(buildHubSessionKey(ctx));
    }

    function pruneHubSessions() {
      const entries = Array.from(state.hubSessions.entries());
      if (entries.length <= MAX_HUB_SESSIONS) return;

      const excess = entries.length - MAX_HUB_SESSIONS;
      for (let i = 0; i < excess; i += 1) {
        state.hubSessions.delete(entries[i][0]);
      }
    }

    function attachWsLogging(ws, ctx) {
      ws.on("message", (rawData, isBinary) => {
        try {
          const data = normalizeWsData(rawData);

          console.log("==== WS MESSAGE ====");
          console.log("WsId:", ctx.wsId);
          console.log("Time:", nowIso());
          console.log("Host:", ctx.host);
          console.log("Path:", ctx.url);
          console.log("WS KIND:", ctx.kind);
          console.log("IsBinary:", isBinary);
          console.log("Length:", data.length);

          if (isBinary) {
            const preview = data.subarray(0, Math.min(data.length, WS_BINARY_HEX_PREVIEW_BYTES)).toString("hex");
            console.log("Binary HEX preview:", preview);
            console.log("====================");

            writeStructuredLog("ws_message_binary", {
              at: nowIso(),
              wsId: ctx.wsId,
              host: ctx.host,
              path: ctx.url,
              kind: ctx.kind,
              length: data.length,
              hexPreview: preview
            });

            handleNeoHubBinaryMessage(ws, ctx, data);
            return;
          }

          const text = data.toString("utf8");
          console.log("Text:", summarizeUtf8(text, WS_TEXT_CONSOLE_LIMIT));
          console.log("====================");

          try {
            const parsed = JSON.parse(text);

            writeStructuredLog("ws_message_json", {
              at: nowIso(),
              wsId: ctx.wsId,
              host: ctx.host,
              path: ctx.url,
              kind: ctx.kind,
              parsed
            });

            handleNeoHubJsonMessage(ws, ctx, parsed);
          } catch {
            writeStructuredLog("ws_message_text", {
              at: nowIso(),
              wsId: ctx.wsId,
              host: ctx.host,
              path: ctx.url,
              kind: ctx.kind,
              text
            });
          }
        } catch (err) {
          console.error("WS message handler failure:", err);
          writeStructuredLog("ws_message_handler_error", {
            at: nowIso(),
            wsId: ctx.wsId,
            host: ctx.host,
            path: ctx.url,
            kind: ctx.kind,
            message: err?.message,
            stack: err?.stack
          });
        }
      });

      ws.on("ping", (data) => {
        const buf = normalizeWsData(data);
        const preview = buf.length ? buf.subarray(0, Math.min(buf.length, 32)).toString("hex") : "";
        console.log("WS ping:", ctx.wsId, ctx.kind, "len=", buf.length, preview ? `hex=${preview}` : "");
      });

      ws.on("pong", (data) => {
        const buf = normalizeWsData(data);
        const preview = buf.length ? buf.subarray(0, Math.min(buf.length, 32)).toString("hex") : "";
        console.log("WS pong:", ctx.wsId, ctx.kind, "len=", buf.length, preview ? `hex=${preview}` : "");
      });

      ws.on("close", (code, reason) => {
        const reasonText = normalizeWsData(reason).toString("utf8");
        console.log("WS close:", ctx.wsId, ctx.kind, code, reasonText);

        writeStructuredLog("ws_close", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          code,
          reason: reasonText
        });
        
        const session = findHubSession(ctx);
        if (session) {
          session.closed = true;
          session.closedAt = nowIso();
          
          if (session?.asrTimer) {
            clearTimeout(session.asrTimer);
            session.asrTimer = null;
          }
          cleanupSessionAudioFiles(session || {});
        }
      });

      ws.on("error", (err) => {
        console.error("WS error:", ctx.wsId, ctx.kind, err);
        writeStructuredLog("ws_error", {
          at: nowIso(),
          wsId: ctx.wsId,
          host: ctx.host,
          path: ctx.url,
          kind: ctx.kind,
          message: err?.message,
          stack: err?.stack
        });
      });
    }

    const server = https.createServer(
      {
        key,
        cert,
        SNICallback: (servername, cb) => {
          console.log("TLS SNI host:", servername);

          if (TLS_DEBUG) {
            console.log("=== TLS SNI ===");
            console.log("Time:", nowIso());
            console.log("Requested servername:", servername);
            console.log("================");
          }

          try {
            const ctx = tls.createSecureContext({ key, cert });
            cb(null, ctx);
          } catch (err) {
            cb(err);
          }
        }
      },
      async (req, res) => {
        let body = Buffer.alloc(0);

        try {
          body = await readBody(req);
        } catch (err) {
          console.error("Failed reading body:", err);
          respondPlanned(res, { statusCode: 500, body: { error: "failed to read body" }, extraHeaders: {} });
          return;
        }

        const record = buildRequestRecord(req, body);
        consoleBanner(record);

        if (
          LOG_BINARY_UPLOAD_PREVIEW &&
          record.method === "PUT" &&
          !record.bodyLooksText &&
          record.bodyLength
        ) {
          writeStructuredLog("binary_upload_preview", {
            at: nowIso(),
            reqId: record.reqId,
            host: record.host,
            method: record.method,
            url: record.url,
            headers: record.headers,
            bodyLength: record.bodyLength,
            bodyHexPreview: record.bodyHexPreview
          });
        }

        const responsePlan = chooseResponse(record);

        record.response = {
          at: nowIso(),
          statusCode: responsePlan.statusCode,
          note: responsePlan.note,
          body: responsePlan.body
        };

        writeStructuredLog(
          `${record.host}_${record.target.servicePrefix || "no_target"}_${record.target.operation || "no_op"}`,
          record
        );

        console.log("==== HTTPS RESPONSE ====");
        console.log("ReqId:", record.reqId);
        console.log("Time:", record.response.at);
        console.log("Status:", responsePlan.statusCode);
        console.log("Note:", responsePlan.note);
        console.log(
          "Body:",
          responsePlan.rawBody !== undefined
            ? responsePlan.rawBody
            : JSON.stringify(responsePlan.body, null, 2)
        );
        console.log("========================");

        respondPlanned(res, responsePlan);
      }
    );

    server.on("connection", (socket) => {
      console.log("=== TCP CONNECTION ===");
      console.log("Time:", nowIso());
      console.log("Remote:", socket.remoteAddress, socket.remotePort);
      console.log("======================");
    });

    server.on("tlsClientError", (err, socket) => {
      if (!TLS_DEBUG) return;
      console.error("=== TLS CLIENT ERROR ===");
      console.error("Time:", nowIso());
      console.error("Remote:", socket.remoteAddress, socket.remotePort);
      console.error("Error name:", err?.name);
      console.error("Error code:", err?.code);
      console.error("Error message:", err?.message);
      console.error(err);
      console.error("========================");
    });

    server.on("secureConnection", (tlsSocket) => {
      if (TLS_DEBUG) {
        console.log("=== TLS SECURE CONNECTION ===");
        console.log("Time:", nowIso());
        console.log("Remote:", tlsSocket.remoteAddress, tlsSocket.remotePort);
        console.log("Authorized:", tlsSocket.authorized);
        console.log("Authorization error:", tlsSocket.authorizationError);
        console.log("Protocol:", tlsSocket.getProtocol?.());
        console.log("Cipher:", tlsSocket.getCipher?.());
        console.log("=============================");
      }

      let seenData = false;

      tlsSocket.on("data", (chunk) => {
        seenData = true;
        if (!TLS_DEBUG) return;
        console.log("=== TLS APP DATA ===");
        console.log("Time:", nowIso());
        console.log("Remote:", tlsSocket.remoteAddress, tlsSocket.remotePort);
        console.log("Bytes:", chunk.length);
        console.log("UTF8 preview:", chunk.toString("utf8", 0, Math.min(chunk.length, 300)));
        console.log("HEX preview:", chunk.subarray(0, Math.min(chunk.length, 100)).toString("hex"));
        console.log("====================");
      });

      tlsSocket.on("end", () => {
        if (!TLS_DEBUG) return;
        console.log("=== TLS SOCKET END ===");
        console.log("Time:", nowIso());
        console.log("Remote:", tlsSocket.remoteAddress, tlsSocket.remotePort);
        console.log("Saw app data:", seenData);
        console.log("======================");
      });

      tlsSocket.on("close", (hadError) => {
        if (!TLS_DEBUG) return;
        console.log("=== TLS SOCKET CLOSE ===");
        console.log("Time:", nowIso());
        console.log("Remote:", tlsSocket.remoteAddress, tlsSocket.remotePort);
        console.log("Had error:", hadError);
        console.log("Saw app data:", seenData);
        console.log("========================");
      });

      tlsSocket.on("error", (err) => {
        if (!TLS_DEBUG) return;
        console.log("=== TLS SOCKET ERROR ===");
        console.log("Time:", nowIso());
        console.log("Remote:", tlsSocket.remoteAddress, tlsSocket.remotePort);
        console.log("Error:", err?.message);
        console.log(err);
        console.log("========================");
      });
    });

    server.on("clientError", (err, socket) => {
      console.error("=== CLIENT ERROR ===");
      console.error("Time:", nowIso());
      console.error("Remote:", socket.remoteAddress, socket.remotePort);
      console.error("Error:", err?.message);
      console.error(err);
      console.error("====================");
    });

    const wss = new WebSocketServer({ noServer: true });

    wss.on("connection", (ws, request) => {
      const ctx = buildWsContext(request);

      logWsConnected(ctx, request);
      attachWsLogging(ws, ctx);

      if (ctx.kind === "api-socket") {
        sendApiSocketGreeting(ws, ctx);
        return;
      }

      if (ctx.kind === "neo-hub-listen" || ctx.kind === "neo-hub-proactive") {
        console.log("Neo-hub connected; waiting for client message");
        return;
      }
    });

    server.on("upgrade", (request, socket, head) => {
      const ctx = classifyWebSocket(request);

      console.log("==== WS UPGRADE ====");
      console.log("Time:", nowIso());
      console.log("Host:", ctx.host);
      console.log("URL:", ctx.url);
      console.log("WS KIND:", ctx.kind);
      console.log("Headers:", JSON.stringify(request.headers, null, 2));
      console.log("====================");

      writeStructuredLog("ws_upgrade", {
        at: nowIso(),
        host: ctx.host,
        url: ctx.url,
        kind: ctx.kind,
        pathTokenKnown: ctx.pathTokenKnown,
        bearerTokenKnown: ctx.bearerTokenKnown,
        transId: ctx.transId,
        robotId: ctx.robotId,
        headers: request.headers
      });

      if (ctx.kind === "unknown") {
        socket.write("HTTP/1.1 404 Not Found\r\n\r\n");
        socket.destroy();
        return;
      }

      if (ctx.kind === "api-socket" && !ctx.pathTokenKnown) {
        socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
        socket.destroy();
        return;
      }

      if (
        (ctx.kind === "neo-hub-listen" || ctx.kind === "neo-hub-proactive") &&
        !ctx.bearerTokenKnown
      ) {
        socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
        socket.destroy();
        return;
      }

      wss.handleUpgrade(request, socket, head, (ws) => {
        wss.emit("connection", ws, request);
      });
    });

    server.listen(PORT, HOST, () => {
      console.log(`Open Jibo Link test server listening on https://${HOST}:${PORT}`);
      console.log(`Structured logs will be written to ${LOG_DIR}`);
    });
