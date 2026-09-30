#!/usr/bin/env node

import WebSocket from "ws";
import { runRealAudioProbe } from "../../../scripts/cloud/real-audio-probe.mjs";

function usage() {
  return `Usage: node src/Jibo.Cloud/node/invoke-real-audio-probe.mjs --audio PATH [options]

Options:
  --audio PATH             Local Ogg/Opus recording (required; maximum 4 MiB)
  --base-url URL           HTTP loopback origin (default http://127.0.0.1:8081)
  --expected-phrase TEXT   Words required in final ASR (default "cloud version")
  --response-mode MODE     cloud-version (default) or transcription (ASR + EOS only)
  --turns COUNT            Repetitions, 1-10 (default 5)
  --timeout-ms MS          Whole batch deadline, 1000-300000 (default 300000)
  --robot-id ID            Synthetic speech-acceptance ID
  --help                   Show this text`;
}

function parseArgs(args) {
  const keys = new Map([
    ["--audio", "audioPath"], ["--base-url", "baseUrl"],
    ["--expected-phrase", "expectedPhrase"], ["--turns", "turns"],
    ["--response-mode", "responseMode"],
    ["--timeout-ms", "timeoutMs"], ["--robot-id", "robotId"],
  ]);
  const options = {};
  for (let index = 0; index < args.length; index++) {
    const key = args[index];
    if (key === "--help") { options.help = true; continue; }
    if (!keys.has(key)) throw new Error(`Unknown option: ${key}`);
    const value = args[++index];
    if (!value || value.startsWith("--")) throw new Error(`${key} requires a value.`);
    options[keys.get(key)] = value;
  }
  return options;
}

function openSocket(url, headers, timeoutMs) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url, { headers, handshakeTimeout: timeoutMs });
    let settled = false;
    const timer = setTimeout(() => fail(), timeoutMs);
    function fail() {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.terminate();
      reject(new Error("Local listen socket could not open."));
    }
    socket.once("open", () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(socket);
    });
    socket.once("error", fail);
    socket.once("unexpected-response", (_request, response) => { response.resume(); fail(); });
  });
}

try {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) console.log(usage());
  else console.log(JSON.stringify(await runRealAudioProbe(options, { openSocket })));
} catch (error) {
  console.error(JSON.stringify({ error: error.message, completedTurns: error.completedTurns ?? [] }));
  process.exitCode = 1;
}
