# Jibo.Cloud.Node

## Overview

This folder contains the original **Node.js prototype** for the OpenJibo cloud layer.

This implementation started as a fast way to stand up a working "fake cloud" so Jibo could begin talking to a replacement backend again. It has been used to map behavior, discover endpoints, observe payloads, and validate real interactions with a live robot.

This is the experimental proving ground for the broader `Jibo.Cloud` effort.

---

## Purpose

The goals of this Node implementation are:

* Reverse engineer Jibo cloud behavior
* Recreate enough of the original cloud to restore functionality
* Capture real request and response data
* Prototype OTA update delivery paths
* Validate speech, jokes, and interaction flows
* Serve as a reference for the C# / .NET implementation

---

## Current Capabilities

This server currently supports:

* HTTPS API handling with `X-Amz-Target` routing
* WebSocket connections for Jibo communication
* Token/session handling (prototype-level)
* Account and robot identity flows (mocked)
* Media, loop, and key endpoints (partial)
* OTA update endpoints (in progress)
* Speech pipeline using:

  * Ogg normalization
  * ffmpeg conversion
  * whisper.cpp transcription
* Basic intent handling (jokes, greetings, time, etc.)
* Skill action responses (speech + simple animations)

---

## package.json

This project uses a minimal Node setup:

```json
:contentReference[oaicite:0]{index=0}
```

Install dependencies with:

```bash
npm install
```

---

## Running the Server

### Requirements

* Node.js
* `ws` package
* `ffmpeg`
* `whisper.cpp` + model
* TLS certificate and key

### TLS Files

Place in working directory:

```plaintext
cert.pem
key.pem
```

### Environment Variables (optional)

```bash
FFMPEG_BIN=/usr/bin/ffmpeg
WHISPER_CPP_BIN=/path/to/whisper-cli
WHISPER_MODEL=/path/to/model.bin
```

### Start

```bash
node open-jibo-link.js
```

Server listens on:

```
https://0.0.0.0:443
```

---

## Getting Jibo to Talk to This Server

This is the part that matters most.

Jibo does not naturally connect to a custom server, so you need to control its network environment and TLS behavior.

### Network Setup (Mango Travel Router)

A simple and effective approach:

* Use a **Mango travel router (~$30)**
* Connect Jibo to this network
* Block outbound internet access
* Force DNS resolution to your server

### DNS Control

On the router:

* Map the following domains to your server:

```
api.jibo.com
api-socket.jibo.com
neohub.jibo.com
```

* Intercept Google DNS requests (hardcoded in Jibo):

```
8.8.8.8
8.8.4.4
```

These must be redirected or blocked so Jibo cannot bypass your DNS.

---

## TLS / Certificate Handling

Jibo expects valid TLS and will reject unknown/self-signed certificates by default.

Because of the older Node runtime and native binaries used on Jibo, this cannot be fully bypassed at the system level.

### Required Changes

You must modify Jibo’s runtime to disable certificate validation:

* Update Node-based modules to allow self-signed certs
* Modify any code using:

```js
rejectUnauthorized: true
```

Change to:

```js
rejectUnauthorized: false
```

* Patch any native or binary services that enforce TLS validation
* Set environment variables where possible to disable strict SSL

This typically requires:

* RCM access to Jibo
* Direct file modification on the device

---

## WiFi Setup (QR Code)

Jibo connects to WiFi using a QR code.

You can generate one here:

https://kevinblog.sytes.net/Jibo/WifiGenerator/

This allows you to easily connect Jibo to your controlled network (such as the Mango router).

---

## OTA Update Direction

One of the most important long-term strategies is leveraging Jibo’s built-in OTA update mechanism.

This server already includes update-related endpoints to support:

* Skill delivery
* Update metadata handling
* Future system updates

The goal is to eventually:

* Deliver updates without requiring device hacking
* Push new functionality directly through Jibo’s native update flow
* Provide a simple recovery path for non-technical users

---

## Logging and Observability

This server is heavily instrumented for debugging and discovery.

It logs:

* Incoming requests and headers
* Target routing (`X-Amz-Target`)
* Responses
* WebSocket activity
* Audio processing stages
* Transcription results

This makes it both a working cloud stub and a reverse engineering tool.

---

## Limitations

This is still a prototype:

* Many endpoints are partial or mocked
* No persistent storage
* Security is minimal
* Configuration is partially hardcoded
* Designed for experimentation, not production

---

## Future Direction

This implementation will evolve into:

* A full C# / .NET cloud service
* Azure-hosted infrastructure
* Trusted SSL with real domains
* Clean OTA update pipeline
* Integration with OpenJibo runtime

The Node version will remain as:

* A reference implementation
* A fast experimentation environment

---

## Notes

This project is part of the OpenJibo effort and is not affiliated with the original Jibo company.

