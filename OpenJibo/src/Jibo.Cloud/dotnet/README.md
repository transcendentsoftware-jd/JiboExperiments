# Jibo.Cloud.DotNet

## Overview

**Jibo.Cloud.DotNet** is the long-term, production-focused implementation of the OpenJibo cloud layer.

While the Node.js implementation was used to rapidly explore and validate Jibo’s cloud behavior, this project represents the future direction: a clean, maintainable, and scalable cloud platform built on **C# and .NET**.

This is where the OpenJibo cloud becomes real.

---

## Vision

The goal of this project is not just to replicate the original Jibo cloud, but to build something better:

* A stable and secure cloud platform for Jibo devices
* A bridge between the physical robot and modern AI-driven systems
* A foundation for new capabilities beyond what Jibo originally supported

This is the backbone of the OpenJibo ecosystem.

---

## Design Principles

### 1. Clean Architecture

This implementation is designed around clear separation of concerns:

* Transport (HTTP, WebSocket)
* Application logic (routing, orchestration)
* Domain models (robot, session, capabilities)
* Integration layers (AI, storage, external services)

---

### 2. Compatibility First

The system will:

* Emulate required Jibo cloud endpoints
* Support existing device expectations
* Preserve OTA update compatibility

This ensures existing devices can reconnect without invasive changes.

---

### 3. Extensibility

The platform is being designed to support:

* New skills and capabilities
* AI-driven conversation and planning
* External integrations (APIs, services, tools)
* Multi-agent orchestration (future CoffeeBreak integration)

---

### 4. Cloud-Native Deployment

The target deployment model includes:

* Azure-hosted services
* Real domains (`openjibo.com`, `openjibo.ai`)
* Proper TLS / certificate chains
* Scalable service architecture

---

## Role in the OpenJibo Architecture

Jibo.Cloud sits between the robot and the runtime:

```plaintext id="l3tq9n"
Jibo Device
   ↓
Jibo.Cloud (this project)
   ↓
OpenJibo Runtime (.NET)
   ↓
Capabilities / AI / Services
```

Responsibilities include:

* Handling device communication (HTTPS + WebSockets)
* Managing identity, sessions, and tokens
* Routing requests to runtime services
* Delivering OTA updates
* Acting as the central coordination layer

---

## OTA Update Strategy

A key part of this implementation is full support for Jibo’s OTA update mechanism.

This enables:

* Delivery of updated skills
* Deployment of new capabilities
* Gradual rollout of OpenJibo runtime components
* A path toward restoring devices without manual intervention

The goal is to make recovery and updates feel native to the device.

---

## Planned Features

This project will evolve to support:

### Core Platform

* HTTPS API endpoints compatible with Jibo
* WebSocket communication layer
* Authentication and token services
* Device and user management

### Runtime Integration

* Conversation orchestration
* Capability routing
* Response planning
* Integration with OpenJibo runtime abstractions

### AI Integration

* Speech-to-text and text-to-speech
* Intent recognition and planning
* External AI providers (pluggable)

### Update System

* OTA update orchestration
* Skill delivery pipeline
* Versioning and rollout control

---

## Project Structure

This folder will contain one or more .NET projects, likely including:

```plaintext id="2qk0a7"
dotnet/
  Jibo.Cloud.Api/          # HTTP + WebSocket endpoints
  Jibo.Cloud.Application/  # Application logic and orchestration
  Jibo.Cloud.Domain/       # Core models and contracts
  Jibo.Cloud.Infrastructure/ # External integrations (storage, AI, etc.)
```

Structure may evolve as the system matures.

---

## Relationship to Node Implementation

The Node.js implementation remains valuable as:

* A reference for endpoint behavior
* A rapid testing environment
* A discovery tool for protocol details

However, this .NET implementation is the intended long-term solution.

---

## Status

This project is in early development.

* Core abstractions are being defined
* Endpoint behavior is being mapped from the Node implementation
* Initial service scaffolding is planned

---

## Contributing

If you're interested in building the future of Jibo, this is the place to do it.

Areas where help is especially valuable:

* API design and endpoint mapping
* WebSocket protocol handling
* OTA update workflows
* AI and conversation systems
* Cloud infrastructure and deployment

---

## Notes

This project is part of the OpenJibo initiative and is not affiliated with the original Jibo company.

The mission is simple:

Bring Jibo back for everyone, technical or not.
Make him what he was meant to be.
Then we make him better.

