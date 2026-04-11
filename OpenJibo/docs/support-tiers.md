# Support Tiers

## Purpose

This document keeps the revival effort honest about what must work first, what can wait for parity, and what belongs to later modernization.

## Required For Core Revive

- hosted replacement cloud reachable through legacy Jibo host routing
- token, account, robot, and session bootstrap flows needed for startup
- basic WebSocket connectivity for listen and proactive channels
- minimal turn handling and a normalized `ResponsePlan` path
- Azure deployment foundation
- Azure SQL-backed persistence design
- bootstrap documentation for router, DNS, RCM, TLS patching, and smoke tests

## Optional For Parity

- broader `X-Amz-Target` family coverage
- richer media management
- more complete key and sharing flows
- higher-fidelity update metadata behavior
- more native-skill bridging and expression parity
- more complete per-version device behavior mapping

## Future Modernization

- OTA-first recovery for non-technical owners
- paid hosted plans, subscriptions, and donation flows
- deeper on-device modernization
- richer runtime orchestration and AI providers
- community plugin or skill ecosystem
- OS, bridge, and firmware modernization beyond hosted-cloud recovery
