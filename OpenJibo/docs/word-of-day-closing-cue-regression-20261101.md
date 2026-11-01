# Word of the Day closing cue: suspected regression

## Owner report and disposition

On November 1, 2026, the owner reported that Word of the Day finishes with the
blue listening ring followed by a sound resembling no-input or misunderstanding.
The owner stayed silent and explicitly reports that this never happened before.
Track this as an **open suspected regression**. Successful protocol completion
does not establish that the audible ending is correct or matches stock behavior.
Do not dismiss the report as normal merely because the optional listen exists.

## Timestamped replay

The monitored game ran at approximately 9:39 AM Central (CST, UTC-6). Cloud
transcription recognized "play word of the day" and the answer "2". Robot logs
record seven successful local empty `word-of-the-day/right_word` results between
15:39:54.239Z and 15:39:56.826Z, followed by a requested idle switch at
15:39:58.940Z and successful idle opening at 15:39:59.054Z. The owner confirmed
that the sound had occurred by the end of this replay.

The installed `WotDRightWord.mim` and `WotDFunFact.mim` both declare a three-second
optional-response listen and zero no-input/no-match GUI retries. The retained
native skill routes an absent grammar transition to `noResponse`, then through
the fun-fact conditional and a `Happy SSA` animation selected from category
`happy` with `ssa-only` metadata before exit. No fun-fact listen was observed in
this particular replay. The SDK restarts early no-input results until the
optional-response deadline expires; the seven replies are consistent with that
previously documented behavior.

The captured ending contains successful local no-input handling and normal skill
exit, with no observed cloud recognition-error reply. The logs do **not** name
the audible sound asset or independently establish whether it came from native
listen closure, the closing animation, or another path. These limits leave the
owner's regression report open.

## Release context

Production was verified on revision `openjibo-cloud--0000061`, image digest
`sha256:c6449ee2cf625c9c0051d4a226915522a867e05499a5dd0083e4dd6d6f485323`.
Release source `e7129949f06a455747e1763e5e05e9f3d4d0ac32` contains the existing
Word of the Day hotfix plus the merged audio batch-gap patch. Exact-source CI
and staging HTTP/WebSocket/two-replica/cross-replica gates passed. The image-only
production update preserved app configuration, identity, scale and the remainder
of the revision template. Staging's original image/template were restored.

The symptom's first introduction has not been established. Neither the Word of
the Day local-routing hotfix nor the later audio timing change is proven causal.
The latter changes hotphrase-launch Ogg endpointing, while the captured final
listen takes the separate completed-word local-no-input path. No rollback,
sound suppression, robot patch or new deployment followed this report.

## Future research

1. Capture synchronized sound/video and robot/cloud events around the exact
   closing cue. Identify the actual selected animation and sound asset without
   changing behavior merely to obtain a trace.
2. Compare against a known earlier or stock completion under a separately
   reviewed test scope. Preserve the distinction between the owner's historical
   observation and a controlled software comparison.
3. Check native listen-state classification, attention/earcon transitions and
   the `Happy SSA` selection. A successful Jetstream turn alone is insufficient
   to clear an audible UX regression.
4. Only after identifying the path, propose a narrow repair that preserves real
   optional speech and normal skill completion. Avoid a global no-input sound
   change, forced idle redirect or blocking reply delay.

Private originals and SHA-256 receipts remain under
`artifact-output/voice-audio-release-20261030/`: `robot-wotd-live-20261101.log`,
`wotd-cloud-session-20261101.json`, the two installed MIM files,
`wotd-replay-findings-20261101.json`, and
`wotd-replay-evidence-hashes-20261101.json`. Keep raw robot logs, identities and
credentials out of public commits. See also the
[earlier completion investigation](word-of-day-stalled-listen-20261028.md).
