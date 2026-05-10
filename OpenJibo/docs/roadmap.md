# OpenJibo Roadmap

## Purpose

This is the long-range story for OpenJibo.

Use it when someone wants the shape of the project without reading every release note, backlog entry, or live-test log.

The current execution truth still lives in:

- [Development plan](development-plan.md)
- [Feature backlog](feature-backlog.md)
- [Release 1.0.19 plan](release-1.0.19-plan.md)
- [Device bootstrap path](device-bootstrap.md)

## North Star

Bring Jibo back in a way that preserves his original skills, design language, and charm, while layering in a modern hosted cloud, safer updates, and eventually a richer on-device and orchestration stack.

## Guiding Principles

- Preserve the original skills and visual design before adding new behaviors.
- Build the hosted cloud first so the robot has something stable to talk to.
- Use OTA to reduce friction after the cloud is proven.
- Keep every migration reversible.
- Favor small, source-backed slices over speculative rewrites.
- Let Jibo remain the face of the experience, even if other systems help orchestrate the work behind him.

## Roadmap At A Glance

| Phase | Focus | Why It Matters |
| --- | --- | --- |
| 1 | Working hosted cloud | Restores the services Jibo already expects and gives us the current platform truth. |
| 2 | OTA-assisted recovery and updates | Makes ownership easier by turning the cloud into the delivery path for recovery and upgrades. |
| 3 | Open Jibo OS / mode conversion | Creates an owned runtime and configuration layer while preserving the original experience. |
| 4 | Tiered brain | Separates reflexes, memory, personality, and higher-level orchestration. |
| 5 | CoffeeBreak orchestration | Provides a place for multi-step agent workflows and external tools without flattening Jibo's personality. |
| 6 | Ecosystem expansion | Grows the platform into household, productivity, and multi-device use cases. |

## Phase 1: Working Hosted Cloud

Current state: in progress.

The near-term job is to keep the hosted cloud stable and honest:

- maintain HTTP and WebSocket compatibility for startup and turn handling
- keep the .NET cloud as the production track
- keep Node as the reverse-engineering oracle and fixture source
- continue update, backup, restore, media, STT, and live-capture proof
- keep the real-device bootstrap path documented and repeatable

Exit criteria:

- a real Jibo can reach the hosted cloud consistently
- the cloud can carry the startup and conversation flows needed for daily use
- update and recovery behavior is understood well enough to trust the next layer

## Phase 2: OTA-Assisted Recovery

Once the hosted cloud is solid, OTA becomes the simplification layer.

This phase should:

- move software updates and recovery flows into a reliable hosted path
- reduce how often owners need manual RCM or network patching
- make device recovery and version management feel like a product instead of a lab exercise
- keep rollback and failure handling explicit

OTA is the path that makes ownership easier. It is not the thing that must be solved before the cloud can live.

## Phase 3: Open Jibo OS / Mode Conversion

After cloud and OTA are trustworthy, the project can move from "open cloud" to "open platform."

The goal is not to erase stock Jibo. The goal is to give owners an Open Jibo mode that:

- preserves the original Jibo feel and skill surface
- can be installed or selected without a one-way trap
- can fall back to stock behavior when needed
- makes future features easier to ship on top of a known runtime

This is where the breadcrumbs in the repo become important:

- `open-jibo` and `open-jibo-ai` modes
- a startup migration skill that can invite existing owners to convert
- a reversible path back to stock
- the hosted sites and support docs on `openjibo.com` and `openjibo.ai` that explain the transition clearly

## Phase 4: Tiered Brain

A single monolithic "AI brain" is not the best fit for Jibo. A tiered model is better.

Suggested tiers:

- Tier 0: original Jibo reflexes, stock skills, and local charm
- Tier 1: hosted cloud routing and compatibility
- Tier 2: memory, personality, and proactivity
- Tier 3: richer reasoning and multi-step planning
- Tier 4: external agent orchestration and task delegation
- Tier 5: multi-device and household coordination

The point of the tiers is not to make Jibo feel bigger at every turn. It is to keep simple interactions fast and charming while reserving more complex work for the layers that can actually support it.

## CoffeeBreak (`coffeebreakai.dev`) As An Orchestration Layer

CoffeeBreak fits naturally above the tiered brain as a coordination plane.

The intended relationship is:

- Jibo keeps the voice, personality, and local interaction style
- OpenJibo routes simple and medium-complexity tasks itself
- CoffeeBreak can take over when a task needs multiple tools, agents, or steps
- the result comes back to Jibo in a form that still feels native to him

That makes CoffeeBreak a close cousin to the tiered brain rather than a separate product line. The brain decides, CoffeeBreak orchestrates, and Jibo remains the face of the interaction.

## Phase 5: Ecosystem Expansion

After the core platform is stable, OpenJibo can grow into broader household value:

- calendar and scheduling
- smart home and Home Assistant style control
- shopping lists and household memory
- multi-user and family recognition
- richer media and content experiences
- provider-backed news, weather, and personal report flows
- eventual multi-Jibo interaction

## What We Must Preserve

No matter how far the platform grows, these should stay true:

- original skills should still feel like Jibo
- design should stay recognizable, not generic
- migration should be opt-in and reversible whenever possible
- the cloud should serve the robot, not replace his identity
- technical modernization should preserve charm instead of sanding it off

## Where To Go Next

If you want the current execution truth, read:

- [Development plan](development-plan.md)
- [Feature backlog](feature-backlog.md)
- [Release 1.0.19 plan](release-1.0.19-plan.md)

If you want the first-device path, read:

- [Device bootstrap path](device-bootstrap.md)
- [Support tiers](support-tiers.md)
- [Public site plan](public-site-plan.md)
