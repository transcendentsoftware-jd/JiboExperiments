# Node Fixtures

These fixtures are sanitized captures derived from the Node protocol oracle and are intended to seed compatibility testing for the .NET port.

Current fixture groups:

- `http/`
  Basic `X-Amz-Target` request and response examples for startup flows.
- `websocket/`
  Sanitized Neo-Hub turn-flow examples used to replay `LISTEN`, `CONTEXT`, `CLIENT_NLU`, `CLIENT_ASR`, buffered-audio accumulation, pending/finalize states, and synthetic `EOS` / `SKILL_ACTION` behavior against the .NET implementation.

Current websocket fixture depth is uneven on purpose:

- `neo-hub-client-asr-joke.flow.json` now asserts a richer vertical slice than reply types alone. It captures the observed Node-oriented `CLIENT_ASR -> LISTEN -> EOS -> delayed SKILL_ACTION` joke turn with payload-shape expectations for `EOS` and joke `SKILL_ACTION`.
- `neo-hub-client-nlu-clock-ask-time.flow.json` captures a real menu-style `CLIENT_NLU` turn from the latest live captures and asserts that `.NET` preserves the observed NLU intent/rules/entities in the synthetic websocket reply instead of flattening everything into generic chat.
- The other websocket fixtures are still mainly sequencing fixtures. They are useful for replay and guardrails, but they should not be read as proof of broader payload parity.

Expand this folder whenever new robot traffic is captured and cleaned.
