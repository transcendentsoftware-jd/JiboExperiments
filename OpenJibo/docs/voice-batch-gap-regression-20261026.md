# Voice capture investigation and partial timing repair — 2026-10-26

The batch-gap repair below addresses a demonstrated premature endpoint decision.
It is not yet a verified fix for the complete stale-audio sequence. The owner's
clarification requires an additional investigation of the audio boundary around
the wake phrase; passing the transport timing tests does not establish that
boundary.

## Observed production build

Read-only Azure checks on October 26 found production `openjibo-cloud` in
`rg-openjibo-prod` serving 100% of traffic from revision `openjibo-cloud--0000059`.
The revision was created September 23 at 12:42:32 UTC and selects image
`cropenjibomanagedrmj4v5utn2.azurecr.io/openjibo-cloud:sha-53d7c7b6315b`.
That tag corresponds to source commit `53d7c7b6315b4400d894f017b99b6d6614ee94eb`.
This is tag-to-source identification, not an independent image-byte attestation.
The public `/health` endpoint reports `1.0.20`. Azure Speech is enabled and
local Whisper is disabled. The turn-finalization and socket-coordinator source
files are unchanged between that source commit and the inspected checkout.

The October experimental Whisper packages and subsequent managed-service registry
work therefore do not explain a newly deployed voice change on this production
revision. The faulty timing change is older and remains deployed.

## User-confirmed incident

The owner confirmed the matching October 26 sequence around 3:59 PM Central
(20:59 UTC). Production console records show:

| UTC | Observation |
| --- | --- |
| 20:59:27 | Finalization starts with three audio pages, 884.5 ms since first audio and 418.0 ms since the preceding batch. Silence threshold: 400 ms; no Ogg end-of-stream. |
| 20:59:28–29 | Recognition first returns `so`, then `so so jimo smart`; the response intent is `not_understood`. |
| 20:59:41 | Weather finalization starts with three audio pages, 845.2 ms since first audio and 419.9 ms since the preceding batch. Again, no end-of-stream. Recognition returns `the weather i do have`; the weather plan keeps the mic open. |
| 20:59:55 | The following listen finalizes with three audio pages, 834.6 ms since first audio and 420.8 ms since the preceding batch. Recognition returns `today's 70`; intent is `not_understood`. |

These logs confirm the self-audio transcript and premature endpoint decisions.
They do not independently establish the exact acoustic boundaries between the
owner's previous speech, wake phrase, new command, and robot playback. A live
robot retest remains necessary to verify the complete reported behavior.

## Owner's clarified sequence and remaining failure

The owner clarified the approximate sequence:

1. Background speech: `Is Jibo super smart`.
2. Owner: `Hey Jibo`, followed by `What's the weather`.
3. Robot: an unrecognized phrase resembling `sunny smart`.
4. Owner repeats `Hey Jibo`, then asks the weather again.
5. Robot gives the sunny/70-degree weather answer.
6. Owner: `Hey Jibo`, followed by `Are you smart`.
7. Robot reports an unrecognized phrase about 70 degrees.

This is selection of speech preceding the intended command, including robot
playback, rather than just a truncated command. The cloud's premature endpoint
can select that leading audio, but it does not explain why that audio is present
or prove that waiting 1,200 ms selects the correct command.

The weather listen started with zero cloud buffered bytes. The later `today's 70`
result came from a newly created cloud session, also with zero buffered bytes at
listen setup. Both transcripts were newly returned by Azure Speech. This rules
out simple reuse of `LastTranscript` or an uncleared cloud audio buffer for these
particular results. It does not rule out earlier speech inside newly transmitted
robot audio, incorrect audio framing, or an ASR result that selects the leading
speech segment instead of the intended command.

The captured stock `LISTEN` shape includes `hotphrase: true`, but does not supply
an explicit sample offset for the wake boundary. The current Azure strategy
decodes the entire accumulated buffer and consumes one `DisplayText`; it has no
selection of a later recognized segment by wake timestamp. The self-audio phrase
heuristic matches stock phrases such as `I heard you`; it is not a general echo
canceller and does not identify an arbitrary weather answer as robot speech.
These are implementation limitations, not proof of which mechanism occurred.

The current Azure CLI identity was denied a Blob Data read of the retained
incident audio using `--auth-mode login`. No access roles or authentication
methods were changed to bypass that restriction. Without those recordings or a
new robot-side capture, the pre-wake/post-wake audio boundary remains unverified.

Full acceptance must replay background speech → wake/weather → weather response
→ wake/`Are you smart`, checking both the audio actually submitted to STT and the
recognized command at each wake. It must also retain normal short commands and
yes/no replies. A mocked transcript or a transport-gap-only test cannot prove
this acoustic behavior.

## Cause and bounded repair

### Robot-side evidence — October 27

Read-only SSH to the owner-supplied `192.168.130.100` identified the same robot,
`Royal-Current-Sage-Canvas`. Its rotated `/var/log/messages.2.gz` was copied and
decompressed locally. The retained log covers the controlled replay. Evidence
is in local `artifact-output/voice-investigation-20261027/`; no services or
settings were changed during collection.

Both detected wake phrases (14:34:19.224 UTC and 14:34:39.374 UTC) report:

```text
word_alignment: 2147483647 2147483647 hey 0.00 2147483647 2147483647 jibo 0.00
RecogSpeakerTD starting at 2147483647
```

All four word boundaries are pinned to the maximum signed 32-bit integer,
despite the wake events being 20 seconds apart. Speaker identification also
reports streaming failures. Historical captures of this same robot have
distinct, advancing boundaries. For example, April 29 14:35:25.102 UTC starts
at 923765952 and April 30 11:35:59.498 UTC at 999400256: an alignment delta of
75,634,304 versus 75,634,396 elapsed milliseconds. That strongly supports
millisecond units for this counter.

At the runtime snapshot, the robot uptime was 52 days 11 hours, and Jetstream
PID 32134 had been running approximately 30.68 days. `/proc/32134/stat` reports
start ticks 188428436; Python `os.sysconf('SC_CLK_TCK')` confirmed 100 ticks per
second. A signed 32-bit millisecond counter reaches its limit after 24.855 days.
Using this process age, a process-relative clock would have crossed that limit
approximately October 21 at 19:57 UTC. The process-relative origin is a
hypothesis; the arithmetic does not prove which clock the recognizer uses.

This is a strong additional explanation for recently appearing failures without
a recent production deployment. A saturated wake boundary could cause the
capture machinery to select the wrong portion of its history buffer. However,
the native implementation has not been traced: the saturation might affect a
logged representation rather than the audio selection itself. Restart/reset
recovery must be tested before asserting this as the full cause.

The robot log also confirms the exact weather TTS input contains `today's high
is 69, and the low is 56`, and later confirms the `hello hello` TTS request.
The weather TTS session ends at 14:34:37.593 UTC; the next wake is detected at
14:34:39.374 UTC. These are service events, not independently measured acoustic
playback endpoints.

Recommended next diagnostic: preserve these files, reset the robot's recognition
clock with a restart, then repeat the same commands and compare both the wake
word boundaries and the cloud transcripts. No cloud patch is needed for that
comparison, keeping the production software constant. Clearing the symptom
would establish a recovery result; prevention of recurrence still needs native
counter analysis.

### Post-reboot voice replay — October 27

The owner reports both questions received correct responses. In the matching production session, Azure Speech transcribed `what's the weather` at 21:17:22 UTC and `are you smart` at 21:17:40 UTC; the resulting plans were `weather` and `robot_knowledge`. Both listen setups had zero buffered bytes and chunks and no prior audio timestamps. Robot logs for Jetstream PID 699 show wake boundaries `10812330 10812510 10812510 10812750` and `10831050 10831215 10831215 10831485`, with speaker starts `10812330` and `10831050` respectively. These distinct advancing boundaries contrast with the pre-reboot values fixed at 2147483647. Sanitized matching evidence is in `artifact-output/voice-investigation-20261027/post-reboot-voice-verification.txt`.

The successful replay and reset of the saturated values strongly support the clock-saturation hypothesis. Reboot also resets other state, so this does not prove the native causal mechanism or permanent prevention. The separate local transport-gap patch remains undeployed.

### Robot reboot — October 27

With the owner's authorization, one normal robot reboot was issued at 18:15:25 UTC. Before reboot, uptime was 52 days 14:14 and Jetstream PID was 32134. SSH returned at 18:17:21 UTC with hostname `Royal-Current-Sage-Canvas`, uptime one minute, and Jetstream PID 699. The robot does not have `systemctl`; a subsequent startup-log check was blocked by a local PowerShell runtime memory error. Physical voice replay and post-reboot wake alignment checks remain pending when the owner returns. This recovery check does not establish whether the audio symptom is fixed.

### Controlled replay — October 27, 9:34 AM Central

The owner reported completing the requested quiet-room weather/`Are you smart`
sequence without a video. The matching production logs show:

| UTC | Observation |
| --- | --- |
| 14:34:22 | First listen opens with an empty cloud audio buffer. |
| 14:34:25–26 | Successive buffered recognitions return `jubo`, `hey jubo what's`, then `hey jibo what's the weather`. The final plan is `weather`. |
| 14:34:39 | A new listen transaction opens in the same session with zero buffered bytes, no prior audio timestamps, and follow-up closed. |
| 14:34:42–43 | Two recognition attempts return empty text; the next returns `today's high is 69 in the lowest 56`. The resulting plan is `hello`. No `Are you smart` transcript appears in the inspected replay window. |

Production still selects revision `openjibo-cloud--0000059` and image
`sha-53d7c7b6315b`; the local timing patch has not been deployed. The early
finalization pattern remains visible: first attempt at three audio pages and
841.7 ms since first audio, using a 400 ms threshold against a 419.1 ms batch gap.

This replay corroborates the earlier-speech recognition in a fresh transaction,
even in the controlled test. It does not establish whether the robot's encoder
queue is delayed, microphone pre-roll includes playback, framing is wrong, or
the recognizer selects the wrong portion. The cloud-recorded text alone does
not establish the acoustic content or wake boundary. `hello` is the cloud plan
intent. The owner confirmed that Jibo said `Hello hello` after the second
question. This agrees with the greeting plan. Recognition returned the weather
sentence rather than the requested smart question, so a changed fallback phrase
alone would not repair the underlying input problem.

### Timing defect

The shared finalizer mistakes ordinary network batching for end-of-speech.
`HandleBinaryAudioAsync` checks elapsed time against the preceding batch before
updating the last-audio timestamp. A 420 ms delivery interval exceeds its 400 ms
threshold even while another audio batch is arriving. The idle watchdog can also
make the same decision between batches.

September 13 performance changes introduced much shorter arrival thresholds
(`41b9b2afdb8d11cf12e367d281ec8aceeaa9b0e9`) and subsequently set the deployed
hotphrase threshold to 400 ms (`d256870659ca6e0ba3ebc2951eb7b9d0d206a150`).
The earlier hotphrase arrival allowance was 1,200 ms. These changes affect Azure
Speech too because endpoint decisions happen before provider selection.

The repair separates the hotphrase Ogg transport gap (1,200 ms) from detected
silence inside the audio (400 ms). Arrival-based early probes use the same
1,200 ms allowance so they cannot bypass the guard. Existing content-silence,
explicit end-of-stream, transcript-hint, continuous-probe and hard-timeout paths
remain available. The fallback arrival path can take up to 800 ms longer when
content silence and explicit end-of-stream are unavailable.

Regression cases cover 420, 500 and 1,000 ms delivery gaps through both inbound
audio and idle processing, followed by an explicit end-of-stream completion.
The original finalizer failed all three delivery-gap cases. With the repair,
the isolated cases passed, followed by all 1,050 selected WebSocket/voice and
socket-coordinator tests (zero failures or skips). The broader selection includes
Opus speech-activity, Azure Speech, Whisper, short-answer, end-of-stream and
concurrent-finalization tests. `git diff --check` also passed.

```powershell
dotnet test OpenJibo/tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~WebSockets|FullyQualifiedName~WebSocketRequestCoordinatorTests' --verbosity quiet
```

The repair is local and has not been deployed. Deployment followed by a physical
robot replay of the same sequence remains the acceptance step for the complete
reported symptom.

No production settings, revision, traffic, microphone behavior policy, or speech
provider were changed during investigation. Background finalization also shares
mutable state with incoming turns, but the incident does not establish that race
as its cause; that separate issue is outside this patch.
