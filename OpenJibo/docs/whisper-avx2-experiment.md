# Optional AVX2 speech experiment

The default `WHISPER_CPU_PROFILE=portable` is unchanged. The explicit `avx2`
profile enables SSE4.2, AVX, AVX2, FMA, F16C and BMI2 while leaving
`GGML_NATIVE=OFF`. It must run only on an x86_64 CPU supporting all those
features. The build target architecture check is not a runtime CPU capability
check. Do not publish this image as the generic portable starter.

On the actual Linux runtime host, run the read-only prerequisite check:

```bash
python3 -B scripts/cloud/preflight-whisper-avx2.py
```

It requires every reported logical processor to expose the six required flags.
Missing evidence fails closed. This does not inspect a remote Docker daemon or
automatically select an image. Recheck after moving to another host or VM.

The Ubuntu acceptance laptop reported an i5-8250U (four cores, eight threads)
and all required flags. Keep `base.en`, four inference threads, audio fixtures,
and the application configuration unchanged. Compare one client first, then
the same two-client overlap test. This profile does not remove the upstream
Whisper server model mutex or introduce parallel model workers.

After obtaining this change, build from `~/JiboExperiments/OpenJibo`:

```bash
docker build --progress plain \
  --build-arg ENABLE_LOCAL_WHISPER=true \
  --build-arg WHISPER_CPU_PROFILE=avx2 \
  --build-arg WHISPER_MODEL=base.en \
  --build-arg WHISPER_BUILD_JOBS=2 \
  -t openjibo-cloud:speech-avx2-test .

docker image inspect openjibo-cloud:speech-avx2-test \
  --format 'ID={{.Id}} Platform={{.Os}}/{{.Architecture}}'
```

This builds only; it does not replace either running stack or the
`openjibo-cloud:self-hosted` baseline tag. Record the source commit and image ID.
Do not use plain Compose `up`: the saved restore override pins the baseline
image and loopback port settings. A reviewed override and rollback procedure
are required before switching the restored test API; keep the original
acceptance stack and backups intact.

## User-reported Ubuntu results, 2026-09-30

Candidate image:
`sha256:548530162f03a63a89de9d5a130f9677cbb21ba7c40a61b88fecf41b770258f6`.
The CMake cache showed all six feature options ON and GGML_NATIVE OFF. The
restored API used that exact image, Azure speech disabled, four threads.
The first three acoustic turns passed in 2775, 1120, and 1079 ms.

The subsequent 30 sequential turns all passed with 30 warm-server completions.
Median was 921 ms, nearest-rank p95 1245 ms, maximum 1592 ms, versus portable
6586.5/6660/7227 ms respectively. Six memory samples ranged 332.9–362 MiB.

Two clients then completed five turns each: all ten passed with ten warm-server
completions. Median was 1504.5 ms and maximum 1940 ms, versus portable
12497.5/12659 ms. Only two resource samples were captured (354.9 and 362.1 MiB);
these cannot establish peak usage. Both runs reported zero restarts and no OOM.

These are short repeated-fixture measurements, not a long soak or representative
speech corpus. They support an opt-in compatible-host variant, not replacing the
portable default. Actual robot playback, broader recognition accuracy, image
provenance/publication and capacity guarantees remain unverified. Source versions
and host conditions were not held to a formal controlled benchmark protocol.

## Next fixture check (not yet run on Ubuntu)

The probe now supports `--response-mode transcription`, requiring final ASR
with the expected words and EOS, but not claiming to validate SKILL_ACTION or
robot playback. Default `cloud-version` behavior is unchanged. Matching is
whole-word inclusion, not exact transcription or a word-error-rate metric.
The mode changes probe assertions only; the application may still execute
recognized intents. Use synthetic, non-sensitive recordings and local test data.

The existing synthetic `known-phrase.ogg` says: "The quick brown fox jumps over
the lazy dog. Please tell me what time it is." Transfer the local fixture to
`~/Downloads/known-phrase.ogg`; it is not included in Git. SHA-256:
`19ccb2ab4d5653ac4081a4b2011dec6370ce38d065b41405866391e1e3c8f827`.

After obtaining the updated probe and checking the fixture hash, use the
existing restored stack (Azure speech must remain disabled):

```bash
node src/Jibo.Cloud/node/invoke-real-audio-probe.mjs \
  --audio ~/Downloads/known-phrase.ogg \
  --base-url http://localhost:8081 \
  --robot-id speech-acceptance-linux \
  --response-mode transcription \
  --expected-phrase "quick brown fox lazy dog time" \
  --turns 3
```

Expected output includes `phraseMatched: true` and `cloudInstruction: null`.
The null is intentional: this mode does not certify a speech response.
This second synthetic recording adds phrase variety, not speaker diversity,
noise robustness, or physical microphone validation. Those remain separate gates.
