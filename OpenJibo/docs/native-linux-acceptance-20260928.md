# Native Ubuntu starter acceptance — 2026-09-28

Evidence below is user-reported terminal output from an independent Ubuntu
laptop, not commands executed by the development agent on that host.

## Environment

- Ubuntu 24.04.4 LTS, x86_64, kernel 7.0.0-31-generic; eight logical CPUs,
  8,190,013,440 bytes RAM. Native-host prerequisite check passed.
- Docker Engine 29.8.1; Compose 5.5.1; local default Unix socket.
- Local image `openjibo-cloud:self-hosted`, Linux/amd64, reported image ID
  `sha256:1a91d211943a53acbdd322a91e530832d15012b37a5e73a17aafc03c66cebe76`.
- Exact source commit was not captured. This is acceptance evidence, not
  signed official build provenance.

## Passed checks

1. Native image build with local Whisper and `base.en` completed.
2. Fresh `openjibo-linux-acceptance` stack: PostgreSQL healthy, migration exit
   zero, API health returned version 1.0.20 on loopback port 8080.
3. Two synthetic identities (`linux-acceptance-1` and `linux-acceptance-2`)
   completed token issuance and notification/listen/proactive socket checks
   in 611 ms. After API restart the same probe passed in 552 ms.
4. With API stopped, SQL found both identities before and after PostgreSQL
   restart. No intervening probe could recreate the records.
5. With API quiesced, state and memory database dumps and the API data volume
   archive were captured, along with a private environment backup.
6. Both databases restored into a separate fresh `openjibo-linux-restore`
   project. SQL found both identities before starting its API.
7. API data archive restored into the separate restore volume. Restored API
   health returned version 1.0.20 on loopback port 8081.
8. Both identities completed all three authenticated socket checks against
   port 8081 in 560 ms.
9. Real-audio probe from main `aabf3f46` completed three same-socket turns
   against the restored API. Fixture checksum matched and the running API's
   `OpenJibo__Stt__EnableAzureSpeech` was `false`. Each turn returned
   LISTEN, EOS, SKILL_ACTION with `phraseMatched` and `cloudInstruction` true.
   Durations were 7578, 7007, and 6690 ms. Provider selection still needs log
   corroboration; these three samples do not establish cold-start causality
   or sustained capacity.
10. A fresh three-turn repeat passed in 5100, 6951, and 6619 ms. A bounded
    log query covering that run counted exactly three
    `whisper-server-buffered-audio` completions, corroborating warm-server use
    for this repeat rather than CLI fallback.

The original stack and backup remain intact. PostgreSQL is not host-published.
Keep the environment backup private: it contains credentials and encryption
settings necessary for recovery. Do not recreate those settings on restore.

## Longer sequential and overlapping runs

The user reported 30/30 sequential warm-server turns passing, with no restart
count increase (zero) and no reported OOM kill. Median latency was 6586.5 ms,
nearest-rank p95 6660 ms, and maximum 7227 ms. Across 33 samples, API-container
memory was 338.7–362.8 MiB; CPU was commonly around 400%. Samples span roughly
three minutes and exclude the PostgreSQL container and whole-host memory.

Two distinct clients then completed five turns each: 10/10 passed with ten
warm-server completions, zero restarts and no reported OOM kill. Median was
12497.5 ms, maximum 12659 ms. Sampled API memory was 365.6–373.6 MiB and CPU
peaked at 494.56%. This is bounded overlap evidence, not sustained capacity.

Source inspection explains an important constraint: the pinned whisper.cpp
v1.9.3 server holds a shared model mutex throughout `/inference`, serializing
inference requests. See upstream
https://github.com/ggml-org/whisper.cpp/blob/v1.9.3/examples/server/server.cpp#L769-L771.
This is consistent with the near doubling of latency, though the run did not
measure queue time separately. Increasing HTTP concurrency does not remove
this model lock; do not remove it around shared model state.

The current Dockerfile also explicitly disables native CPU tuning, SSE4.2,
AVX, AVX2, FMA, F16C and BMI2. That favors portability, but may leave significant
performance unused. Next experiment: establish the laptop's CPU capabilities,
then evaluate an explicitly opt-in optimized image without replacing the
portable default. Keep model, thread count and fixtures unchanged for the
comparison. A multi-worker model pool is a separate memory/concurrency design,
not a safe assumption about a server thread-count flag.

## Remaining gaps

Initial socket checks used `--skip-turn`; the subsequent acoustic test passed.
These results do not establish proactive transaction responses, physical robot
playback, or performance under sustained load. Restoring the memory database does not
prove decryption of representative encrypted user data; no such fixture was
verified. Local image build success alone does not prove Whisper was invoked.

Next: provider verification, bounded longer speech tests, and physical robot acceptance.
Official publication, signatures, and hybrid/managed acceptance remain separate.

## Repeating the acoustic test

The reusable probe is `src/Jibo.Cloud/node/invoke-real-audio-probe.mjs`.
It is restricted to an HTTP loopback origin and a synthetic
`speech-acceptance-*` identity. It creates/uses that identity and sends actual
binary audio without an ASR text hint. This is not a read-only probe.
It requires final ASR containing the expected words, EOS, and a cloud-version
SKILL_ACTION instruction on every turn. It does not play audio or prove which
STT provider ran. Keep Azure speech disabled and verify local-provider evidence.

Use a short Ogg/Opus recording saying “Please tell me your cloud version.”
The previously tested synthetic fixture is not committed to the repository;
transfer it separately to `~/Downloads/cloud-version.ogg`. Its SHA-256 is
`afbeb0cc617df9ccaa0f5312265e1b91e7dd714a36dd3563ce98b3fb16801484`.
Do not substitute an arbitrary personal recording or send it to a public host.

After obtaining the new probe files and fixture, run from `OpenJibo`:

```bash
sha256sum ~/Downloads/cloud-version.ogg
docker compose -p openjibo-linux-restore exec -T api printenv OpenJibo__Stt__EnableAzureSpeech
node src/Jibo.Cloud/node/invoke-real-audio-probe.mjs \
  --audio ~/Downloads/cloud-version.ogg \
  --base-url http://localhost:8081 \
  --robot-id speech-acceptance-linux \
  --turns 3
```

Stop if the checksum differs or the Azure setting is not `false`.
The three turns share one socket and the entire batch is bounded to five
minutes. Report the JSON result, not raw tokens, `.env`, or unfiltered logs.
The transcript and returned ESML are deliberately excluded from probe output.
Passing proves these acoustic/response transactions only, not a load test,
physical playback, warm-server-versus-CLI selection, or offline certification.
