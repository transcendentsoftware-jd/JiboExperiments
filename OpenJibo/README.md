# Hybrid Jibo Runtime Plan

## Goal

Build a **modern local-first Jibo runtime** while preserving the parts of native Jibo that are still useful:

* native wake/turn plumbing where helpful
* native skills where helpful
* native embodiment and rendering
* fast experimentation in **.NET 10** off-robot

Jibo’s native runtime already exposes a layered service model centered around **Jetstream** for turn/event flow, **GlobalManagerService** for routing, **SkillsService** for skill lifecycle, and **ExpressionService** for embodiment/rendering. The SSM startup is config-driven and mode-driven, which suggests a hybrid mode is a viable path.   

---

## Architecture Direction

We will keep the **main experimental runtime in .NET 10** and treat Jibo as an embodied endpoint with a thin bridge layer.

That means:

* **off-robot**: conversation logic, planning, AI routing, capabilities
* **on-robot**: thin adapter/bridge to native Jibo services
* **native Jibo**: reuse rendering, skill hosting, and useful event seams

---

## High-Level ASCII Flowchart

```text
+--------------------------------------------------------------+
|                     NATIVE JIBO LAYER                        |
|--------------------------------------------------------------|
| Wake / Turn Events                                           |
|  - Jetstream                                                 |
|  - hjHeard / turn started / turn result                      |
|                                                              |
| Native Services                                              |
|  - GlobalManagerService                                      |
|  - SkillsService                                             |
|  - ExpressionService                                         |
|  - TTS / Body / Visual / Motion services                     |
+------------------------------+-------------------------------+
                               |
                               | events / hooks / commands
                               v
+--------------------------------------------------------------+
|                     JIBO BRIDGE LAYER                        |
|--------------------------------------------------------------|
| Thin adapter between Jibo and modern runtime                 |
|                                                              |
| Responsibilities:                                            |
|  - receive turn/wake events                                  |
|  - receive skill context / native state                      |
|  - forward normalized events to .NET runtime                 |
|  - accept ResponsePlans / commands from .NET runtime         |
|  - invoke native skills / expression / TTS / visuals         |
+------------------------------+-------------------------------+
                               |
                               | normalized turn context
                               v
+--------------------------------------------------------------+
|                  MODERN .NET 10 RUNTIME                      |
|--------------------------------------------------------------|
|  Conversation Broker                                         |
|    - session state                                           |
|    - follow-up windows                                       |
|    - topic/context tracking                                  |
|                                                              |
|  STT Strategy Selector                                       |
|    - native transcript                                       |
|    - local STT                                               |
|    - cloud STT                                               |
|                                                              |
|  Brain Strategy Selector                                     |
|    - skill/rules path                                        |
|    - local AI                                                |
|    - cloud AI                                                |
|    - hybrid routing                                          |
|                                                              |
|  Action / Orchestration Planner                              |
|    - gestures / visuals / ESML / delegation                  |
|    - capability/tool calls                                   |
|    - build final ResponsePlan                                |
|                                                              |
|  Capability Registry                                         |
|    - weather / time / reminders / tools                      |
|    - native skill delegation                                 |
|    - robot expression helpers                                |
+------------------------------+-------------------------------+
                               |
                               | ResponsePlan / commands
                               v
+--------------------------------------------------------------+
|                    EXECUTION TARGETS                         |
|--------------------------------------------------------------|
|  - Native SkillsService                                      |
|  - Native ExpressionService                                  |
|  - Native TTS / visuals / motion                             |
|  - Local AI backends                                         |
|  - Cloud AI backends                                         |
|  - External APIs / tools                                     |
+--------------------------------------------------------------+
```

---

## Runtime Flow

```text
[Wake Word / Turn / Follow-up]
              |
              v
      [Jibo Native Events]
              |
              v
        [Jibo Bridge Layer]
              |
              v
     [Conversation Broker (.NET)]
              |
              v
     [STT Strategy Selection]
              |
              v
    [Brain Strategy Selection]
      /          |           \
     /           |            \
[Skill/Rules] [Local AI] [Cloud AI]
      \           |            /
       \          |           /
              [Planner]
                 |
                 v
         [ResponsePlan Built]
                 |
                 v
          [Jibo Bridge Layer]
                 |
                 v
 [Skills / Expression / TTS / Motion / Visuals]
                 |
                 v
      [Follow-up Window or Timeout]
```

---

## Planned Hybrid Mode

Jibo’s startup and service composition are mode-driven and config-driven, so the long-term plan is to add a **new custom mode** rather than replacing stock behavior outright.  

### Candidate mode names

* `hybrid`
* `openjibo`
* `revival`
* `local-first`

### Intent of the mode

The custom mode should:

* preserve normal mode for stock behavior
* preserve developer mode for native debugging
* enable the bridge/runtime path for hybrid experiments
* allow selective routing between old and new Jibo behavior

---

## Design Principles

### 1. Keep Jibo-specific code at the edges

The .NET runtime should know about:

* turns
* sessions
* plans
* capabilities
* render actions

It should **not** depend directly on:

* Electron internals
* SSM implementation quirks
* old Linux deployment constraints

### 2. Reuse native embodiment

Native Jibo rendering is valuable. ExpressionService appears to own animation, attention, DOF arbitration, and embodied output, so it should be reused as long as possible. 

### 3. Replace cognition before replacing embodiment

The first thing to modernize is:

* routing
* planning
* AI selection
* follow-up conversation behavior

Not necessarily:

* body motion
* TTS
* expression plumbing

### 4. Favor thin robot-side code

The bridge on Jibo should stay small and stable. Fast-moving logic belongs in .NET 10.

### 5. Everything should converge to a ResponsePlan

Regardless of source:

* skill
* rules engine
* local AI
* cloud AI

the result should become a single normalized response/output plan.

---

## Native Jibo Mapping

Based on current reverse engineering, the native service boundaries map roughly like this: Jetstream is the turn/event seam, GlobalManagerService performs routing and skill-launch logic, SkillsService manages skill lifecycle, and ExpressionService handles embodiment/rendering. 

```text
Our Concept                    Native Jibo Equivalent
----------------------------  --------------------------------
Wake / Turn Source            Jetstream
Conversation Broker           split across Jetstream + routing
Brain Selection               GlobalManagerService + skills
Skill Execution               SkillsService
Renderer / Embodiment         ExpressionService
```

---

## Proposed Project Layout

```text
/src
  /Jibo.Runtime
    Core runtime orchestration
    - ConversationBroker
    - Session state
    - Turn pipeline
    - ResponsePlan builder

  /Jibo.Runtime.Abstractions
    Interfaces and models
    - ITurnSource
    - ISttStrategy
    - IBrainStrategy
    - IResponsePlanner
    - IRobotAdapter
    - TurnContext
    - ResponsePlan

  /Jibo.Bridge
    Jibo adapter / compatibility layer
    - robot event ingestion
    - command dispatch back to Jibo
    - native hook integration

  /Jibo.Brain.Rules
    deterministic routing / skills / decision tree

  /Jibo.Brain.Local
    local AI experiments

  /Jibo.Brain.Cloud
    cloud AI experiments

  /Jibo.Capabilities
    tools and callable capabilities
    - weather
    - time
    - reminders
    - skill delegation
    - expression helpers

  /Jibo.Simulator
    fake robot target for testing ResponsePlans

/docs
  architecture
  notes
  traces
```

---

## Initial Build Plan

### Phase 1 — Contracts and runtime skeleton

Build the core models and interfaces first:

* `TurnContext`
* `ConversationSession`
* `SttResult`
* `BrainDecision`
* `ResponsePlan`
* `RenderAction`
* `FollowupPolicy`

### Phase 2 — Minimal broker

Implement:

* session open/close
* follow-up timeout
* topic/context tracking

### Phase 3 — Bridge skeleton

Create the adapter boundary for:

* inbound Jibo events
* outbound robot commands

Even if the first version is mocked, keep the interface stable.

### Phase 4 — First working path

Implement a narrow vertical slice:

* input turn
* decision/rules path
* weather example
* TTS response
* follow-up window

### Phase 5 — Native integration expansion

Add native delegation for:

* skills
* expression
* visuals
* gestures
* local turn/open follow-up behavior

### Phase 6 — Hybrid AI routing

Add:

* local AI path
* cloud AI path
* confidence/routing policy

---

## First Vertical Slice

Recommended first demonstration:

### Example

User says:

> Hey Jibo, what’s the weather?

System flow:

1. Jibo event arrives through bridge
2. .NET broker opens a session
3. transcript enters routing
4. weather capability is called
5. planner builds a `ResponsePlan`
6. bridge sends speech + visual action back to Jibo
7. follow-up window stays open

Then:

> What about the low tonight?

The same session stays active without wake word if the follow-up window is still open.

---

## Near-Term Questions to Answer

* What is the cleanest robot-side bridge seam:

  * Jetstream hook
  * skill hook
  * local service calls
  * mixed approach

* What is the smallest command set needed to drive Jibo usefully:

  * speak
  * gesture
  * visual
  * launch skill
  * keep listening

* Which pieces should remain native the longest:

  * expression
  * skill hosting
  * turn engine
  * wake-word flow

* How should custom mode selection activate the hybrid path

---

## Practical Strategy

For now:

* **develop fast in .NET 10**
* **use Jibo as an embodied endpoint**
* **keep the robot-side integration thin**
* **delay deep on-robot porting until architecture proves itself**

This keeps experimentation fast while preserving a path toward deeper integration later.

---

## Current Working Hypothesis

The best long-term shape is:

```text
stock Jibo embodiment + modern external cognition + thin hybrid bridge
```

That gives us:

* rapid iteration
* local-first experiments
* preserved native robot personality/expression
* reduced dependence on brittle legacy cloud paths
