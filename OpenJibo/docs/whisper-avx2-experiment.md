# Optional AVX2 speech experiment

The default `WHISPER_CPU_PROFILE=portable` is unchanged. The explicit `avx2`
profile enables SSE4.2, AVX, AVX2, FMA, F16C and BMI2 while leaving
`GGML_NATIVE=OFF`. It must run only on an x86_64 CPU supporting all those
features. The build target architecture check is not a runtime CPU capability
check. Do not publish this image as the generic portable starter.

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

Offline build-contract tests pass, but successful compilation, acoustic accuracy,
runtime CPU compatibility and performance improvements remain unverified until
the candidate is built and exercised on the laptop. No performance gain is
promised by the profile alone.
