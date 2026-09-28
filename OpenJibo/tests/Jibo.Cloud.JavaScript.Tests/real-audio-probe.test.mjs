import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import test from "node:test";
import {
  normalizeRealAudioOptions,
  phraseMatches,
  runRealAudioProbe,
  splitOggOpusPages,
} from "../../scripts/cloud/real-audio-probe.mjs";

function oggPage(body) {
  const payload = Buffer.from(body);
  const page = Buffer.alloc(28 + payload.length);
  page.write("OggS");
  page[26] = 1;
  page[27] = payload.length;
  payload.copy(page, 28);
  return page;
}

const audio = Buffer.concat([oggPage("OpusHead"), oggPage("a"), oggPage("b"), oggPage("c"), oggPage("d")]);

test("accepts only HTTP loopback origins and bounded synthetic turns", () => {
  for (const url of ["http://127.0.0.1:8081", "http://localhost:8081", "http://[::1]:8081"])
    assert.equal(normalizeRealAudioOptions({ audioPath: "sample.ogg", baseUrl: url }).baseUrl.origin, new URL(url).origin);
  for (const url of ["https://127.0.0.1", "http://example.com", "http://127.0.0.1:8081/path",
    "http://user:password@127.0.0.1", "http://127.0.0.1:8081/?x=1"])
    assert.throws(() => normalizeRealAudioOptions({ audioPath: "sample.ogg", baseUrl: url }), /loopback origin/);
  assert.throws(() => normalizeRealAudioOptions({ audioPath: "sample.ogg", turns: 11 }), /1 through 10/);
  assert.throws(() => normalizeRealAudioOptions({ audioPath: "sample.ogg", timeoutMs: 300001 }), /300000/);
  assert.throws(() => normalizeRealAudioOptions({ audioPath: "sample.ogg", robotId: "real-robot" }), /synthetic/);
});

test("requires bounded Ogg/Opus pages and matches complete words", () => {
  assert.equal(splitOggOpusPages(audio).length, 5);
  assert.throws(() => splitOggOpusPages(audio.subarray(0, -1)), /incomplete/);
  assert.throws(() => splitOggOpusPages(Buffer.concat([oggPage("notopus"), ...Array(4).fill(oggPage("a"))])), /Ogg\/Opus/);
  assert.equal(phraseMatches("Please tell me your cloud version.", "cloud version"), true);
  assert.equal(phraseMatches("cloudy versions", "cloud version"), false);
});

class FakeSocket extends EventEmitter {
  sent = [];
  terminated = false;

  send(data, options) {
    this.sent.push({ data, options });
    if (typeof data !== "string") return;
    const request = JSON.parse(data);
    if (request.type !== "CLIENT_ASR") return;
    queueMicrotask(() => {
      for (const reply of [
        { type: "LISTEN", data: { asr: { final: true, text: "Please tell me your cloud version." } } },
        { type: "EOS" },
        { type: "SKILL_ACTION", data: { action: { config: { jcp: { config: { play: { esml: "Your cloud version is ready." } } } } } } },
      ]) this.emit("message", Buffer.from(JSON.stringify({ ...reply, transID: request.transID })), false);
    });
  }

  terminate() { this.terminated = true; }
}

test("sends binary pages, never sends transcript hints, and completes three real audio turns", async () => {
  const socket = new FakeSocket();
  const fetchCalls = [];
  const result = await runRealAudioProbe({ audioPath: "sample.ogg", turns: 3 }, {
    readAudio: async () => audio,
    fetchImpl: async (url, options) => {
      fetchCalls.push({ url, options });
      return { ok: true, json: async () => ({ token: "secret-token" }) };
    },
    openSocket: async (url, headers) => {
      assert.equal(url.pathname, "/v1/listen");
      assert.equal(headers.Authorization, "Bearer secret-token");
      return socket;
    },
  });
  assert.equal(result.turns.length, 3);
  assert.deepEqual(result.turns.map((turn) => turn.replyTypes),
    Array(3).fill(["LISTEN", "EOS", "SKILL_ACTION"]));
  assert.equal(socket.sent.filter((message) => Buffer.isBuffer(message.data)).length, 15);
  assert.equal(socket.sent.filter((message) => typeof message.data === "string").length, 6);
  assert.equal(socket.sent.some((message) => typeof message.data === "string" &&
    message.data.includes("cloud version")), false);
  assert.equal(fetchCalls[0].options.headers["X-Amz-Target"], "Account_20160715.CreateHubToken");
  assert.equal(fetchCalls[0].options.redirect, "error");
  assert.equal(JSON.stringify(result).includes("secret-token"), false);
  assert.equal(socket.terminated, true);
});

test("protocol failures do not expose transcript or token", async () => {
  const socket = new FakeSocket();
  socket.send = function (data, options) {
    this.sent.push({ data, options });
    if (typeof data !== "string" || JSON.parse(data).type !== "CLIENT_ASR") return;
    const transID = JSON.parse(data).transID;
    queueMicrotask(() => this.emit("message", Buffer.from(JSON.stringify({
      type: "LISTEN", transID, data: { asr: { final: true, text: "private speech secret-token" } },
    })), false));
  };
  await assert.rejects(runRealAudioProbe({ audioPath: "sample.ogg", turns: 1 }, {
    readAudio: async () => audio,
    fetchImpl: async () => ({ ok: true, json: async () => ({ token: "secret-token" }) }),
    openSocket: async () => socket,
  }), (error) => !error.message.includes("private speech") && !error.message.includes("secret-token") &&
    /expected phrase/.test(error.message));
  assert.equal(socket.terminated, true);
});
