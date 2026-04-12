# Node Fixtures

These fixtures are sanitized captures derived from the Node protocol oracle and are intended to seed compatibility testing for the .NET port.

Current fixture groups:

- `http/`
  Basic `X-Amz-Target` request and response examples for startup flows.
- `websocket/`
  Sanitized Neo-Hub turn-flow examples used to replay `LISTEN`, `CONTEXT`, `CLIENT_NLU`, `CLIENT_ASR`, and synthetic `EOS` / `SKILL_ACTION` behavior against the .NET implementation.

Expand this folder whenever new robot traffic is captured and cleaned.
