# Jibo.Cloud

## Overview

**Jibo.Cloud** is the replacement cloud layer for the OpenJibo project.

The original Jibo relied heavily on cloud services for core functionality including speech, skills, configuration, and identity. With the original cloud infrastructure no longer available, this project aims to recreate and eventually improve that layer so Jibo can function again for everyday users.

This is not just a mock or emulator. The goal is to build a functional, extensible cloud platform that can support both the original Jibo behaviors and new capabilities over time.

---

## Current Approach

The cloud layer is being developed in stages.

To move quickly and understand Jibo’s behavior, development started with a lightweight Node.js implementation that acts as a “fake cloud.” This allows rapid experimentation, endpoint discovery, and validation of how Jibo communicates.

As the system stabilizes, the implementation is being ported to **C# / .NET** for long-term maintainability, performance, and integration with hosted infrastructure.

---

## Architecture Direction

The long-term vision for Jibo.Cloud is:

* Provide a stable replacement for Jibo’s original cloud endpoints
* Support secure communication (TLS) using a real hosted domain
* Act as a bridge between the physical robot and the OpenJibo runtime
* Enable new capabilities beyond the original Jibo feature set

### OTA Update Strategy

One of the key strategies for restoring and extending Jibo functionality is leveraging its existing **OTA (over-the-air) update mechanism**.

Rather than requiring users to manually modify their devices, Jibo.Cloud aims to:

* Deliver updates through Jibo’s native update flow
* Push new or modified skills directly to the robot
* Eventually enable delivery of larger system updates (including OpenJibo components)

This approach significantly lowers the barrier for non-technical users and creates a path toward a true “plug-and-play” recovery experience.

---

## Hosting Strategy

The cloud service is intended to be hosted publicly using domains such as:

* `openjibo.com`
* `openjibo.ai`

Final domain structure is still being evaluated and may include subdomains similar to the original Jibo architecture.

---

## Project Structure

```plaintext id="6h2v1k"
Jibo.Cloud/
  node/      # Initial prototype implementation (Node.js)
  dotnet/    # Long-term implementation (C# / .NET)
```

---

## Goals

* Restore functionality to existing Jibo devices
* Provide an “easy button” for non-technical users
* Leverage OTA updates to simplify delivery and adoption
* Keep the system open and extensible for the community
* Build a foundation for future OpenJibo capabilities

---

## Status

This project is actively in development.

* Node.js prototype: in progress and functional for basic interactions
* C# implementation: planned and in progress

---

## Contributing

If you're interested in helping, exploring, or building on this work, contributions are welcome.

The goal is to make Jibo accessible again, not just for developers, but for anyone who owns one.

---

## Notes

This project is not affiliated with the original Jibo company. It is a community-driven effort to restore and extend the platform.

