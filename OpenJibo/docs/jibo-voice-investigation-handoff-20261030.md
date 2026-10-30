# Jibo voice investigation handoff — October 30, 2026

Continue the Jibo voice and Word of the Day work in `transcendentsoftware-jd/JiboExperiments`. Read the applicable repository instructions first. The owner prefers GPT-6.1 Terra for delegated work. Work locally without deploying or restarting the robot unless the next request authorizes that action.

## Saved branches and their relationship

- `origin/codex/word-of-day-local-completion`: isolated production hotfix, commit `57ef364f9e5c453bdab7192be1149a8c97d55f70`, based directly on the former deployed source `53d7c7b6315b4400d894f017b99b6d6614ee94eb`. Only the Word of the Day local-completion change and its tests were released.
- `origin/codex/jibo-voice-investigation-handoff`: continuation branch based on local main `426e256c`, containing equivalent Word of the Day changes, the separate undeployed audio batch-gap candidate and regression test, and these investigation notes. It is a combined working snapshot. Do not blindly cherry-pick the hotfix onto it: the same behavior is already present. Compare both branches and account for their different base commits before preparing an integration PR or release.

## Read these files

- `OpenJibo/docs/voice-batch-gap-regression-20261026.md`
- `OpenJibo/docs/word-of-day-stalled-listen-20261028.md`
- `OpenJibo/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/WebSocketTurnFinalizationService.cs`
- `OpenJibo/tests/Jibo.Cloud.Tests/WebSockets/AudioBatchGapRegressionTests.cs`
- The Word of the Day tests in `OpenJibo/tests/Jibo.Cloud.Tests/WebSockets/JiboWebSocketServiceTests.cs`.

## Findings and deployment boundary

The old-audio/weather failure strongly correlates with robot wake timestamps saturating at signed 32-bit maximum `2147483647` after long recognizer uptime. One owner-authorized reboot on October 27 reset the timestamps; a weather / Are you smart replay then succeeded. This is recovery evidence, not a permanent robot patch or proof of the exact native overflow mechanism. No permanent robot-side fix exists yet. That reboot permission was consumed.

The separate cloud timing candidate changes hotphrase Ogg arrival-gap finalization from 400 ms to 1,200 ms, while retaining 400 ms audio-content silence. It addresses demonstrated premature completion on ordinary 420–500 ms robot batches. It does not establish a fix for stale pre-wake audio. Three regression cases cover 420/500/1,000 ms gaps; the earlier broad verification passed 1,050 selected WebSocket/coordinator tests. The candidate has NOT been deployed. Assess command latency, short responses, EOS, content silence, cancellation and concurrent finalization before any release.

The Word of the Day hotfix sends local-rule-only LISTEN/EOS for `word-of-the-day/right_word`, removing the mixed global rules and forced idle redirect. Last verified production was `openjibo-cloud--0000060`, image digest `sha256:fd108d3f2e6bed2cbb056890c2506afa191f811ed50171be6447327495b2308b`, source `57ef364f`. Recheck live state before assuming this is still current. This was an image-only update with no database migration or production configuration changes. Exact-source staging gate: GitHub Actions run `40780297006`, attempt 2; HTTP, WebSocket, two-replica and cross-replica committed-read checks passed. The focused suite passed 55 tests at release time.

The owner's October 29 proscenium game completed: definition, optional fun fact, brief listening, then idle at 12:57:14 PM Central. The original stuck winning screen did not recur. Eight immediate right-word replies over 2.735 seconds were internal retries: the native SDK reopens early no-input results until its three-second optional-response window expires. The observed retry window had eight ignored-audio events and zero STT strategy selections. Do not infer CPU or billing cost from this. No optimization was implemented: a safe improvement requires a cancellable transaction-scoped deadline, not a blocking reply delay. The current socket coordinator awaits reply sending inline; adding DelayMs can postpone handling later messages.

## Suggested continuation

The combined continuation snapshot was verified on October 30 with 58 tests passed, zero failed and zero skipped:

```powershell
dotnet test OpenJibo/tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj --no-restore --filter 'FullyQualifiedName~AudioBatchGapRegressionTests|FullyQualifiedName~WordOfDay|FullyQualifiedName~LocalNoInput|FullyQualifiedName~YesNo' --verbosity quiet
```

1. Verify remote branch contents and current deployment, then review and integrate the equivalent Word of the Day hotfix into the intended mainline without pulling unrelated newer changes into a production release.
2. Review and validate the undeployed batch-gap candidate independently. Preserve the distinction between a cloud endpointing defect and the robot clock hypothesis. Prepare a reviewable PR if appropriate; deployment is a separate decision.
3. Pursue native timestamp analysis if preventing the robot failure is the priority. Do not claim the reboot permanently repaired it.
4. Treat optional-response retry reduction as a separate optimization unless new evidence establishes a functional problem.

## Local evidence and operational cautions

Raw robot logs and stock SDK/skill copies remain locally under `C:\Projects\JiboExperiments\artifact-output\voice-investigation-20261027\`; they are intentionally excluded from GitHub. They include `proscenium-messages.log`, `flummox-live-messages.log`, `jibo-sdk.js`, `word-of-day-native-index.js`, clock evidence and post-reboot verification. Read locally if needed; do not upload raw logs or credentials casually. The committed notes preserve the key findings for another machine.

Robot address last supplied by owner: `192.168.130.100`. Production resource group: `rg-openjibo-prod`; staging: `rg-openjibo-staging`, app `openjibo-cloud`. Do not confuse it with `openjibo-managed-api`. Azure Log Analytics workspace: `2a4679ba-116b-413a-8a41-a5479bbff3e6`, table `ContainerAppConsoleLogs_CL`. Authentication and access must be checked in the active environment; previous Blob Data RBAC denial was not bypassed.

Staging was restored after release to original image digest `sha256:c043f92998cb3106ad42a33f6b7cf0c7028978a93e49fc65bf18b9f11ca51dd0`, revision `0000164`, scale 1–2, replay-observer flag True, registry Legacy/admin enabled. These are historical observations, not instructions to overwrite current state. A different conversation's catalog test retained a restoration-fingerprint exception; do not relabel that proof as passing. Its exclusive staging hold was released after our deployment; coordinate any new shared staging work afresh.

The nested `C:\Projects\JiboExperiments\OpenJiboCloud` checkout and unrelated artifact directories belong to other work. Preserve them. Do not add all workspace files to a commit. The original hotfix worktree is `C:\Users\User\.codex\worktrees\word-day-production-fix\JiboExperiments`.
