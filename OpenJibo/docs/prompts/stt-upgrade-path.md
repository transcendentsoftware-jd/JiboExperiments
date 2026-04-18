# STT Upgrade Path Prompt

Improve the OpenJibo `.NET` speech-to-text path for live robot testing.

Current repo context:

- workspace root: `C:\Projects\JiboExperiments\OpenJibo`
- current live captures from `2026-04-18` showed that some turns succeeded, but many buffered-audio turns failed before producing a usable transcript
- the current local `.NET` STT path is in:
  - `src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Audio/LocalWhisperCppBufferedAudioSttStrategy.cs`
  - `src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Audio/OggOpusAudioNormalizer.cs`
  - `src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/WebSocketTurnFinalizationService.cs`
  - `src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/DefaultSttStrategySelector.cs`
- Node remains the oracle for current behavior:
  - `src/Jibo.Cloud/node/open-jibo-link.js`
- live test evidence and guidance are documented in:
  - `docs/development-plan.md`
  - `docs/live-jibo-test-runbook.md`
  - `src/Jibo.Cloud/dotnet/README.md`

Observed problems to ground the work:

- one captured run could not find `whisper-cli` at the configured rooted path
- many buffered-audio turns failed because `ffmpeg` rejected the normalized Ogg output
- we need a more reliable path for testing than the current partially working local whisper chain

Goals:

1. review the current `.NET` STT seam and compare it against the Node preprocessing flow
2. recommend and implement the best next STT path for testing, preferring reliability and simplicity over novelty
3. keep the STT integration behind the existing abstractions so we can swap providers later
4. preserve or improve telemetry so failed turns clearly show whether the problem is decode, tool lookup, provider failure, or unusable transcript quality
5. update tests and docs to match the chosen direction

Constraints:

- do not remove the synthetic transcript-hint path; it is still valuable for fixture replay and parity
- do not assume Azure-hosted STT is automatically the answer unless the codebase and testing needs support that choice
- prefer an implementation that is easy for other revival-group testers to run consistently
- avoid large speculative architecture changes that are not needed for a near-term v1 testable cloud

Deliverables:

- code changes for the improved STT path
- tests covering strategy selection, success, and failure handling
- doc updates with exact setup guidance and a recommendation on whether local whisper remains optional, fallback-only, or deprecated for testing
- a short summary of the tradeoffs and why the chosen path is the best next step
