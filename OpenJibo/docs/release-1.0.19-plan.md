# Release `1.0.19` Plan

## Purpose

This release starts the shift from `1.0.18` hardening to visible feature growth.

The goal is to keep compatibility work steady while shipping personality and capability slices that make OpenJibo feel less like a placeholder cloud and more like a real assistant platform.

## Snapshot

- Kickoff date: `2026-05-05`
- Cloud version source of truth: [OpenJiboCloudBuildInfo.cs](../src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Services/OpenJiboCloudBuildInfo.cs)
- Active release constant: `1.0.19`

## Scope

### 1. Persona And Identity Surface

- add natural voice responses for robot identity/personality prompts
- start building reusable content hooks for question-vs-command style responses
- keep first implementation rule-based and test-backed

### 1a. Original Personalized Function Inventory

Keep a running checklist of the legacy persona questions and identity surfaces we want to preserve or port:

- identity and origin: `what are you`, `who are you`, `what is Jibo`, `who made you`, `where are you from`
- persona and capability: `do you have a personality`, `what is your job`, `how much do you know`, `what do you want`
- self-description and social charm: `what's your name`, `do you have a nickname`, `do you like being Jibo`, `are there others like you`
- favorite-style prompts: `what is your favorite color`, `what is your favorite food`, `what is your favorite music`
- attraction and preference prompts: `what is your favorite flower`, `do you like R2D2`, `do you like the sun`, `do you like space`, `do you like kids`
- capability and charm prompts: `can you laugh`, `can you dance`
- affect and mood: `how are you`, `are you happy`, `are you sad`, `are you angry`
- memory and identity recall: `who am i`, `what is my name`, `when is my birthday`, `what is my favorite music`
- greeting and presence charm: `good morning`, `welcome back`, `who is this`, person-aware greeting follow-ups
- recognition follow-ups: `do you know me`, `do you remember me`, `can you recognize me`
- seasonal and contextual charm: holiday prompts, pizza day, surprise offers, personal report personality hooks
- conversational follow-ups that should stay local and warm instead of falling into generic chat

Current batch note:

- `favorite color`, `favorite food`, and `favorite music` are the first small favorites-family slice
- the next source-backed batch now includes `favorite flower`, `R2D2`, `sun`, `space`, `kids`, plus a couple of charm prompts like `can you laugh` and `can you dance`
- the follow-up mood batch now includes `how are things`, `how is your day`, `are you sad`, and `are you angry`
- the personality follow-up batch now includes `what are you up to` and `what are you doing` so small talk stays warm and local instead of falling into generic chat
- the descriptor batch now includes `are you kind`, `are you funny`, `are you helpful`, `are you curious`, `are you loyal`, `are you mischievous`, and `are you likable`
- the seasonal batch now includes `what holidays do you celebrate`, New Year's resolution questions, `happy holidays`, `what halloween costume`, spring suggestions, and holiday gift prompts
- the latest social batch adds `welcome back`, `what are you thinking`, `what have you been doing`, and `what did you do` so presence and charm stay lively without distracting from the memory roadmap
- this pass keeps Build B moving while still favoring source-backed phrasing and preserving the command-vs-question boundary
- the next passes should keep the same pattern and prefer source-backed phrasing whenever the legacy MIM text is available
  - if a source-backed legacy line is missing, use a temporary direct reply only to keep the pass moving, then backfill source text later
  - after the favorites batch, the next doc pass should focus on richer persona follow-ups and the remaining memory/presence charm surfaces
- Build B is now reserved for the next source-backed scripted-response batch:
  - `how do you work`
  - `what do you eat`
  - `where do you live`
  - `where were you born`
  - `what languages do you speak`
  - `what do you like to do`
  - `what are you made of`
  - `what is your favorite flower`
  - `do you like R2D2`
  - `do you like the sun`
  - `do you like space`
  - `do you like kids`
  - `can you laugh`
  - `can you dance`

The goal is to port these in small batches, capture the source-backed phrasing where possible, and keep a test for each batch so the list never becomes a vague backlog graveyard.

### 2. Reliability And Device Proof

- complete update/backup/restore proof path with captures and operator docs
- continue alarm/gallery/yes-no cleanup from `1.0.18` evidence where regressions are still open
- improve short-turn STT reliability and low-signal screening

### 3. Pegasus-To-Cloud Platform Porting

- prioritize small source-backed slices from Pegasus/JiboOS that can be shipped safely
- keep Nimbus and stock payload compatibility as the release guardrail
- avoid broad subsystem rewrites without tests and live-capture evidence
- keep the legacy prompt inventory visible in the backlog so porting stays paced and traceable

### 4. Holidays And Seasonal Personality

- port holiday-aware personality responses as a visible extension of the new persona slice
- start with a small, source-backed set (for example birthdays/holidays already represented in legacy data paths)
- ensure holiday responses feel characterful while still routing through stock-compatible payloads

### 5. Multi-Tenant Memory Storage Foundation

- define tenant boundaries across account, loop, device, and person-memory records
- add storage abstractions that can move from in-memory/local JSON to hosted SQL/Blob without reworking behavior layers
- implement memory-ready schemas and repository contracts for user facts (names, birthdays, personal dates, preferences) with strict tenant scoping
- seed person-aware state keys now so future interactions can scope to account + loop + device + person without another shape change
- keep stateful interaction flows repository-backed instead of embedding more ad hoc metadata in the websocket layer

### 6. Multi-Server Sync Path

- document the eventual sync boundary for stateful data that should move between servers
- treat the first pass as repository-local durability, then layer replication and conflict handling on top
- prefer explicit change records or versioned state snapshots over implicit last-writer wins when we outgrow a single node
- keep cross-server reconciliation out of the hot path until the single-server semantics are stable

## First Implemented Slice In `1.0.19`

The first delivered slice in this release is persona expansion:

- `how old are you`
- `when's your birthday`
- `do you have a personality`
- `make a pizza`

`make a pizza` is now wired to the legacy scripted-response identity (`RA_JBO_MakePizza`) with pizza-making animation ESML, based on the original skill manifests.

This slice is intentionally small and user-visible. It creates immediate personality gains while we keep deeper platform work in parallel.

## Second Implemented Slice In `1.0.19`

The second delivered slice is first tenant-scoped personal memory:

- store birthday from phrases like `my birthday is April 12`
- recall birthday from phrases like `when is my birthday`
- store preferences from phrases like `my favorite music is jazz`
- recall preferences from phrases like `what is my favorite music`

Memory keys are scoped by account/loop/device tenant context so one tenant does not leak into another.

## Third Implemented Slice In `1.0.19`

The third delivered slice starts memory-triggered proactivity and broadens memory parsing:

- `surprise me` now runs a weighted proactivity selector
- selectors use tenant-scoped memory signals (favorites and likes/dislikes) plus date triggers
- February 9 (`National Pizza Day`) can proactively launch the pizza animation path
- proactive pizza fact offer flow now stores pending offer state and resolves direct `yes` / `no` follow-up answers
- memory parsing now covers:
  - names (`my name is ...`, `what is my name`)
  - important dates (`our anniversary is ...`, `when is our anniversary`)
  - likes/dislikes (`i like ...`, `i love ...`, `i dislike ...`, `i don't like ...`)
  - favorite phrase variants including reverse form (`pizza is my favorite food`)

## Fourth Implemented Slice In `1.0.19`

The fourth delivered slice starts weather compatibility using Pegasus-style report-skill routing:

- weather phrases now route to `report-skill` instead of generic placeholder chat
- outbound NLU launch uses legacy reactive intent `requestWeatherPR` (source-aligned with Pegasus manifests/tests)
- weather entity hints are added for:
  - `date = tomorrow` on tomorrow phrasing
  - `Weather = rain|snow|...` on condition questions (for example `will it rain tomorrow`)
- websocket output now emits local skill redirect + silent completion for weather launch, matching existing local-skill launch patterns

## Fifth Implemented Slice In `1.0.19`

The fifth delivered slice adds provider-backed weather content while preserving Pegasus launch compatibility:

- OpenWeather provider abstraction and infrastructure wiring are added to the hosted cloud
- weather requests still launch `report-skill` with `requestWeatherPR` and legacy weather/date entities
- weather replies now include cloud-generated spoken summaries from provider data:
  - current conditions (`Right now in ...`)
  - tomorrow forecast shape (`Tomorrow in ...`) with high/low temperatures when available
- simple location extraction is supported for phrasing like `what's the weather in Chicago tomorrow`
- provider config supports appsettings and `OPENWEATHER_API_KEY` environment fallback for deployment

## Personality Import Ladder

This is the practical plan for importing legacy Jibo `mims` into OpenJibo without pretending we already have a full Pegasus runtime.

### What Is Possible Today

OpenJibo can already host a meaningful subset of legacy personality content because it has:

- a shared catalog for content-driven replies
- chitchat state-machine routing with route metadata
- outbound payload support for `skillId`, `mim_id`, `mim_type`, `prompt_id`, `prompt_sub_category`, and ESML
- existing examples that already behave like legacy MIMs for pizza, dance, news, weather, and generic chat

### What We Need To Build

To move from hand-wired examples to broader imports, we need three small platform pieces:

1. a MIM inventory importer that can scan the legacy tree and produce a normalized catalog
2. a prompt-selection layer that can choose by `skill_id`, `mim_id`, prompt category, and condition metadata
3. a safe ESML/prompt renderer that preserves existing stock-compatible payload shapes

### What Can Be Ported With Each Build

#### Build A: Declarative Prompt Packs

Port immediately:

- `core-responses`
- `deflector`
- the simplest `emotion-responses`
- any `scripted-responses` that are just direct prompt lists with no special state machine

Why these first:

- they are already close to the current `JiboExperienceCatalog` model
- they give us user-visible personality quickly
- they are the best fit for low-risk testing tomorrow

#### Build B: Conditioned Prompt Packs

Port after the importer and renderer are in place:

- `gqa-responses`
- structured emotion responses with `condition` gates
- prompt sets that select different replies by user state or Jibo state

Why these next:

- they are still mostly declarative
- they need a small amount of condition evaluation, but not a new conversation engine

#### Build C: Conversation Families

Port after Build B:

- richer `scripted-responses` families that depend on follow-up state
- special-date / holiday personality sets
- more nuanced chitchat branches that need context-aware routing

Why these later:

- they need state and follow-up behavior, not just prompt selection
- they are where personality feels most alive, but they are also where bugs will be easiest to introduce

#### Build D: Full Parity Cleanup

Port after the core ladder is stable:

- large cross-skill collections
- any MIMs that depend on Pegasus-only parser assumptions
- any files that need a dedicated runtime abstraction instead of catalog lookup

## System Diagram Alignment Snapshot (`2026-05-06`)

Legacy architecture (`system_diagram.png`) has been mapped to current OpenJibo cloud services so release execution stays anchored to:

- where we were (Pegasus/Jibo cloud design intent)
- where we are (current hosted `.NET` modular monolith)
- where we are headed (durable memory, proactivity catalogs, parser depth, provider aggregation)

Reference:

- [system-diagram-alignment.md](system-diagram-alignment.md)

## Greetings And Presence Planning Snapshot (`2026-05-07`)

Pegasus greeting and presence behavior has now been captured into a source-anchored OpenJibo implementation plan.

Reference:

- [greetings-presence-plan.md](greetings-presence-plan.md)

## Live Validation Snapshot (`2026-05-07`)

User-confirmed end-to-end behavior now includes:

- `Hey Jibo -> What's your cloud version?` (working)
- `Hey Jibo -> What's the time?` (working)
- `Hey Jibo -> Surprise me -> pizza fact -> $YESNO (Yes) -> fact` (working)
- `Hey Jibo -> Surprise me -> pizza fact -> $YESNO (No) -> decline reply` (working)

This confirms the pizza-fact offer state now keeps the yes/no branch open through completion and does not require a second wake-word reset for the follow-up answer.

## Personal Report Planning Snapshot (`2026-05-07`)

Personal report parity planning is now captured with Pegasus source anchors for weather visuals/animations, live news, commute, and calendar gap coverage.

Reference:

- [personal-report-parity-plan.md](personal-report-parity-plan.md)

## Next Queued Task (`2026-05-06`)

Queued next `1.0.19` implementation task (now started):

- dialog parsing expansion and ambiguity guardrails

Execution focus:

- import additional Pegasus parser phrases/entities into intent handling where safe
- reduce trigger-only captures that drop the rest of the utterance
- preserve command-vs-question personality split and local skill payload compatibility
- add focused tests for new phrase families and ambiguity boundaries
- keep listener-state observability aligned with the legacy GLSM flow while phrase guardrails are added

First completed guardrail slice under this queue:

- GLSM listener flow capture + telemetry mapping
- stale pending-listen recovery path for long-open no-context/no-audio listens

Second completed guardrail slice under this queue:

- tightened date/time ambiguity handling (`what's your birthday`/`what's your bday` no longer falls into date intent)
- expanded Pegasus-inspired memory/weather phrase coverage:
  - birthday alias parsing (`my bday is ...`, `when is my bday`)
  - shorthand preference sets (`my favorite sport football`)
  - weather variants (`what's today's weather look like`, `will it be sunny tomorrow`)
- listener continuation guardrail now differentiates incomplete preference fragments from complete shorthand preference sets

Third completed guardrail slice under this queue:

- expanded Pegasus `userLikesThing` / `userDislikesThing` / `doesUserLikeThing` / `doesUserDislikeThing` phrase-family coverage
  - includes additional dislike/negation variants (`loathe`, `did not like`, `didn't enjoy`, `don't really like`)
  - includes group-preference variants (`we like`, `we love`, `we dislike`, `we can't stand`)
  - includes lookup variants (`do you think i like ...`, `do you believe i don't like ...`)
- added affinity set/lookup attempt guardrails so partial captures route to affinity prompts instead of generic chat
- extended auto-finalize continuation deferral for the new Pegasus affinity stems (`we like`, `i loathe`, and related variants)
- added focused interaction + websocket tests for the new parser/guardrail behavior

Next queued implementation track after parser guardrails:

- personal report parity slices (weather visual parity, live news path, commute/calendar gap closure)

First completed slice in this personal-report parity track:

- added provider-ready news briefing path with Nimbus-compatible `news` payload continuity
- preserved fallback behavior when no live provider is configured
- added memory/transcript category hinting for provider requests (`sports`, `technology`, `business`, etc.)
- added provider-side request caching for both news and weather to reduce integration churn and repeated lookups
- added focused interaction + websocket tests for provider-backed news speech output and request-hint plumbing

## Next Slices

1. MIM import foundation for personality expansion
2. Dialog parsing expansion
3. Presence-aware greetings and identity-triggered proactivity
4. Personal report parity slices
5. Holidays and seasonal personality slice beyond pizza day
6. Durable memory persistence path
7. Update/backup/restore end-to-end proof
8. STT noise-screening and short-utterance reliability pass
9. Provider-backed news expansion and deeper weather parity
10. Capture indexing and retention boundary for group testing

For slice 1, use the new import ladder above to keep the work grounded in what OpenJibo can already render today versus what needs new scaffolding.
For slices 2-5, use Pegasus phrase lists, MIM IDs, and behavior patterns as the source anchor before broadening into OpenJibo-native improvements.

## Definition Of Done

Release `1.0.19` is complete when:

- planned slices have focused tests and updated docs
- regression checklist passes for the existing stock-OS compatibility paths
- live runs confirm no critical regressions in alarms, gallery, yes/no, and cloud-version diagnostics
- memory/personality storage proves tenant isolation by account/loop/device boundaries and is compatible with the target hosted cloud footprint
