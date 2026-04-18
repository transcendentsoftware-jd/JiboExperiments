# Live Jibo .NET Test Runbook

## Goal

Run the first real `Jibo -> .NET OpenJibo cloud` test on the Ubuntu machine using the same working certificate and controlled routing that currently work with the Node server.

This runbook intentionally avoids introducing Azure, new hostnames, or new robot bootstrap changes during the first live test.

## Recommended Approach

Use the existing Ubuntu networking path and certificate material first.

- keep the current controlled Wi-Fi / routing arrangement
- keep the current Jibo-facing hostnames:
  - `api.jibo.com`
  - `api-socket.jibo.com`
  - `neo-hub.jibo.com`
- keep the Node server available as a fallback
- run the `.NET` API with the same cert/key material by converting it to a temporary `.pfx` for Kestrel

## Prerequisites On Ubuntu

Install or confirm these tools:

- `dotnet`
- `openssl`
- `curl`
- `python3`

Optional but useful:

- `pwsh`

`pwsh` is not required anymore for the Ubuntu live test path if you use the bash/python helpers added here.

## Certificate Plan

The Node server currently uses:

- `cert.pem`
- `key.pem`

The `.NET` API can reuse that same material for the test by converting it at startup into a temporary `.pfx`.

If your current cert file already includes the working chain, use it as-is.

If your chain is separate, pass it as `CHAIN_PEM`.

## Step By Step

1. On Ubuntu, stop the Node server if it is currently bound to port `443`.

2. From the repo root, start the `.NET` cloud using the same cert/key:

```bash
./scripts/cloud/start-dotnet-with-node-cert.sh
```

Optional environment overrides:

```bash
CERT_PEM=/path/to/cert.pem \
KEY_PEM=/path/to/key.pem \
CHAIN_PEM=/path/to/chain.pem \
ASPNETCORE_URLS="https://0.0.0.0:443;http://0.0.0.0:24605" \
./scripts/cloud/start-dotnet-with-node-cert.sh
```

3. In another terminal, run the prep checklist:

```bash
./scripts/cloud/invoke-live-jibo-prep.sh
```

By default this uses the local HTTP port exposed by the launcher:

- `http://localhost:24605`

That avoids certificate-name validation issues during preflight.

If you want to override it, either of these works:

```bash
BASE_URL=http://localhost:24605 ./scripts/cloud/invoke-live-jibo-prep.sh
BASEURL=http://localhost:24605 ./scripts/cloud/invoke-live-jibo-prep.sh
```

4. Verify controlled routing from the Ubuntu environment:

```bash
./scripts/bootstrap/test-openjibo-routing.sh
```

5. Power on Jibo and let it connect using the existing controlled network configuration.

6. Perform the first live checks in this order:

- startup / bootstrap reachability
- one simple chat turn
- one joke turn

7. After the run, summarize the captured websocket telemetry:

```bash
./scripts/cloud/get-websocket-capture-summary.sh
```

8. Inspect exported fixtures under:

- `captures/websocket/fixtures/`

Telemetry from the same run should also now be present under:

- `captures/http/`
- `captures/websocket/`

9. Import the best fixture into the checked-in websocket fixture set:

```bash
python3 ./scripts/cloud/import-websocket-capture-fixture.py \
  /path/to/exported.flow.json \
  neo-hub-real-jibo-first-chat
```

10. Keep notes on:

- whether startup succeeded cleanly
- which websocket paths connected
- whether audio stayed pending or finalized
- whether EOS timing matched expectations
- whether any unexpected message families appeared

## Latest Test Notes To Carry Forward

The most recent live round showed that startup and some Q-and-A paths are progressing, but audio-turn reliability is still uneven.

Carry these expectations into the next run:

- constrained yes/no replies should be tested intentionally because they need special handling and are easy to miss if STT drifts
- phrases intended to trigger known skills should be repeated using a small, documented wording set so we can separate routing issues from Whisper errors
- provider-backed placeholder answers are still expected for weather, commute, calendar, news, and similar routes unless that feature path is explicitly implemented

For STT during live testing:

- prefer runs where `audioTranscriptHint` or other synthetic replay cues are available
- do not assume local `whisper.cpp` success means the audio pipeline is stable overall
- if many turns stay pending or `ffmpeg` rejects normalized Ogg files, treat that as a speech-pipeline issue first, not an intent-mapping issue
- keep the Node server available as the comparison path for yes/no and audio-preprocessing behavior

## What To Do If The Test Fails

If the robot does not connect or the first turn fails:

1. confirm the `.NET` API is actually bound on `443`
2. confirm the cert presented by the `.NET` API matches the currently working Node cert path
3. confirm the Ubuntu routing still points Jibo traffic at the same machine
4. compare the `.NET` websocket capture output with prior Node logs
5. compare the `.NET` HTTP capture output with prior Node logs
6. temporarily switch back to Node to confirm the environment still works

## Not In Scope For This First Test

Do not mix these into the first live run:

- Azure deployment cutover
- new permanent OpenJibo hostnames
- IaC rollout
- new device bootstrap edits beyond the already working setup

Those are valid next steps, but they should follow the first successful `.NET` live capture, not precede it.
