import { readFile } from "node:fs/promises";

const MAX_AUDIO_BYTES = 4 * 1024 * 1024;
const MAX_PAGE_BYTES = 1024 * 1024;
const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "[::1]"]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

export function normalizeRealAudioOptions(options = {}) {
  assert(typeof options.audioPath === "string" && options.audioPath.trim(), "--audio is required.");
  const rawUrl = options.baseUrl ?? "http://127.0.0.1:8081";
  let baseUrl;
  try { baseUrl = new URL(rawUrl); }
  catch { throw new Error("--base-url must be an HTTP loopback origin."); }
  assert(baseUrl.protocol === "http:" && LOOPBACK_HOSTS.has(baseUrl.hostname) &&
    !baseUrl.username && !baseUrl.password && baseUrl.pathname === "/" &&
    !baseUrl.search && !baseUrl.hash, "--base-url must be an HTTP loopback origin.");
  const robotId = options.robotId ?? "speech-acceptance-local";
  assert(typeof robotId === "string" && /^speech-acceptance-[a-z0-9-]{1,40}$/.test(robotId),
    "--robot-id must be a synthetic speech-acceptance identity.");
  const expectedPhrase = options.expectedPhrase ?? "cloud version";
  assert(typeof expectedPhrase === "string" && /^[a-z]+(?: [a-z]+)*$/.test(expectedPhrase) &&
    expectedPhrase.length <= 100, "--expected-phrase must contain 1-100 lowercase ASCII letters and spaces.");
  const turns = Number(options.turns ?? 5);
  assert(Number.isInteger(turns) && turns >= 1 && turns <= 10, "--turns must be an integer from 1 through 10.");
  const timeoutMs = Number(options.timeoutMs ?? 300_000);
  assert(Number.isInteger(timeoutMs) && timeoutMs >= 1_000 && timeoutMs <= 300_000,
    "--timeout-ms must be an integer from 1000 through 300000.");
  return { audioPath: options.audioPath, baseUrl, robotId, expectedPhrase, turns, timeoutMs };
}

export function splitOggOpusPages(buffer) {
  assert(Buffer.isBuffer(buffer) && buffer.length <= MAX_AUDIO_BYTES,
    "Audio must be an Ogg/Opus file of at most 4 MiB.");
  const pages = [];
  for (let offset = 0; offset < buffer.length;) {
    assert(offset + 27 <= buffer.length && buffer.toString("ascii", offset, offset + 4) === "OggS",
      "Audio contains an incomplete or invalid Ogg page.");
    const headerEnd = offset + 27 + buffer[offset + 26];
    assert(headerEnd <= buffer.length, "Audio contains an incomplete Ogg segment table.");
    let bodyLength = 0;
    for (let index = offset + 27; index < headerEnd; index++) bodyLength += buffer[index];
    const pageEnd = headerEnd + bodyLength;
    assert(pageEnd <= buffer.length && pageEnd - offset <= MAX_PAGE_BYTES,
      "Audio contains an incomplete or oversized Ogg page.");
    pages.push(buffer.subarray(offset, pageEnd));
    offset = pageEnd;
  }
  assert(pages.length >= 5 && pages.some((page) => page.includes(Buffer.from("OpusHead"))),
    "Audio must contain at least five Ogg/Opus pages.");
  return pages;
}

export function phraseMatches(transcript, expectedPhrase) {
  const words = new Set((transcript.toLowerCase().match(/[a-z]+/g) ?? []));
  return expectedPhrase.split(" ").every((word) => words.has(word));
}

export async function runRealAudioProbe(options, { fetchImpl = globalThis.fetch, openSocket, readAudio = readFile } = {}) {
  const config = normalizeRealAudioOptions(options);
  assert(typeof fetchImpl === "function" && typeof openSocket === "function", "HTTP and socket adapters are required.");
  let audio;
  try { audio = await readAudio(config.audioPath); }
  catch { throw new Error("Could not read the audio file."); }
  const pages = splitOggOpusPages(audio);
  const controller = new AbortController();
  const deadline = Date.now() + config.timeoutMs;
  const timer = setTimeout(() => controller.abort(), config.timeoutMs);
  let socket;
  try {
    let response;
    try {
      response = await fetchImpl(config.baseUrl, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Amz-Target": "Account_20160715.CreateHubToken",
          "X-Jibo-RobotId": config.robotId,
          "X-OpenJibo-AppVersion": "1.0.20",
        },
        body: JSON.stringify({ deviceId: config.robotId }),
        signal: controller.signal,
        redirect: "error",
      });
    } catch { throw new Error("Local CreateHubToken request failed or timed out."); }
    assert(response.ok, `Local CreateHubToken returned HTTP ${Number(response.status) || 0}.`);
    let payload;
    try { payload = await response.json(); }
    catch { throw new Error("Local CreateHubToken returned invalid JSON."); }
    assert(typeof payload?.token === "string" && payload.token, "Local CreateHubToken did not issue a Hub token.");
    const wsUrl = new URL("/v1/listen", config.baseUrl);
    wsUrl.protocol = "ws:";
    try { socket = await openSocket(wsUrl, { Authorization: `Bearer ${payload.token}` }, Math.max(1, deadline - Date.now())); }
    catch { throw new Error("Local listen socket could not open."); }
    const completedTurns = [];
    for (let turn = 1; turn <= config.turns; turn++) {
      const transID = `${config.robotId}-${Date.now()}-${turn}`;
      const started = performance.now();
      const replyTypes = [];
      let finalAsr = false;
      let cloudInstruction = false;
      const result = await new Promise((resolve, reject) => {
        let settled = false;
        const remaining = Math.max(1, deadline - Date.now());
        const turnTimer = setTimeout(() => finish(new Error("Speech batch timed out.")), remaining);
        function cleanup() {
          clearTimeout(turnTimer);
          socket.off("message", onMessage);
          socket.off("close", onClose);
          socket.off("error", onError);
        }
        function finish(error) {
          if (settled) return;
          settled = true;
          cleanup();
          if (error) reject(error);
          else resolve({ turn, replyTypes, phraseMatched: true, cloudInstruction: true,
            durationMs: Math.round(performance.now() - started) });
        }
        function onClose() { finish(new Error("Local listen socket closed early.")); }
        function onError() { finish(new Error("Local listen socket failed.")); }
        function onMessage(raw, isBinary) {
          if (isBinary) return;
          let reply;
          try { reply = JSON.parse(raw.toString()); }
          catch { finish(new Error("Local listen socket returned invalid JSON.")); return; }
          if (reply?.transID !== transID) return;
          if (typeof reply.type === "string" && /^[A-Z_]{1,32}$/.test(reply.type)) replyTypes.push(reply.type);
          if (reply.type === "LISTEN" && reply.data?.asr?.final === true) {
            const transcript = reply.data.asr.text;
            if (typeof transcript !== "string" || !transcript.trim()) {
              finish(new Error("Local listen socket returned a blank ASR transcript.")); return;
            }
            if (!phraseMatches(transcript, config.expectedPhrase)) {
              finish(new Error("ASR transcript did not contain the expected phrase.")); return;
            }
            finalAsr = true;
          }
          if (reply.type === "SKILL_ACTION") {
            const esml = reply.data?.action?.config?.jcp?.config?.play?.esml;
            if (typeof esml !== "string" || !/cloud version/i.test(esml)) {
              finish(new Error("Cloud response did not contain the expected speech instruction.")); return;
            }
            cloudInstruction = true;
          }
          if (finalAsr && cloudInstruction && replyTypes.includes("EOS")) finish();
        }
        socket.on("message", onMessage);
        socket.on("close", onClose);
        socket.on("error", onError);
        try {
          socket.send(JSON.stringify({ type: "LISTEN", transID, data: { rules: ["wake-word"] } }));
          for (const page of pages) socket.send(page, { binary: true });
          socket.send(JSON.stringify({ type: "CLIENT_ASR", transID, data: {} }));
        } catch { finish(new Error("Could not send audio to the local listen socket.")); }
      }).catch((error) => {
        error.completedTurns = [...completedTurns];
        throw error;
      });
      completedTurns.push(result);
    }
    return { robotId: config.robotId, expectedPhrase: config.expectedPhrase, turns: completedTurns };
  } finally {
    clearTimeout(timer);
    socket?.terminate();
  }
}
