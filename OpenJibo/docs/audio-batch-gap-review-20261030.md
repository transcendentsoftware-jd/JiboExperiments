# Audio batch-gap review — October 30, 2026

This branch is stacked on the [Word of the Day mainline integration](https://github.com/transcendentsoftware-jd/JiboExperiments/pull/298).
Its production-code diff changes only hotphrase-launch Ogg endpoint timing:
1,200 ms arrival gaps and early arrival probes, while preserving 400 ms
audio-content silence. It does not apply the Word of the Day fix a second time.

The isolated WebSocket/coordinator selection passed 1,051 tests with no failures
or skips. Four batch-gap cases cover 420, 500 and 1,000 ms gaps without premature
recognition, explicit EOS completion, and a positive 1,300 ms idle-gap fallback
with the minimum three audio-bearing pages. Existing content-silence, short
answer, yes/no, EOS and concurrent finalization contracts remain covered.

The fallback can add up to 800 ms compared with the old 400 ms arrival decision
when EOS and content silence are unavailable. Physical command-latency and
acoustic-boundary acceptance are still required before deployment. This timing
candidate has not been deployed and does not prove a repair for stale pre-wake
audio or the robot's saturated recognizer timestamp. The separate
[investigation](voice-batch-gap-regression-20261026.md) retains those evidence
limits and the historical reboot recovery; no permanent robot patch exists.

Restore emitted existing NU1902/NU1903 advisories for
`System.Security.Cryptography.Pkcs` 9.0.13. Neither voice patch changes that
dependency. A test-only companion correction keeps the outbox integration
fixture dates recent, preserving their chronology and the unchanged production
35-day event-validity rule.

No deployment, reboot, speech-provider switch, database migration or live-window
reuse accompanies this review. Merge the Word of the Day integration first,
then retarget this audio PR to main and recheck its exact diff and CI. A later
source-bound staging/robot acceptance window requires separate authorization.
