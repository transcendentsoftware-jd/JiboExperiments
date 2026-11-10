# Closed runtime maintenance profile

Set `OpenJibo__Maintenance__Enabled=true` before starting the API to keep an
installation closed while configuring or maintaining it. The process answers
GET/HEAD `/health/live` with 200 and `/health/ready` with 503. Every other request
returns 404, including robot protocol, WebSockets, portal, static assets and POST
requests. Responses use `Cache-Control: no-store`.

The profile is selected at startup, before runtime data, speech, protocol and
provider services are registered. Normal environment/appsettings loading still
occurs. Missing data/provider configuration does not prevent a closed process
from serving liveness, and no application runtime store is initialized. Framework
request logging is restricted to warnings/errors; robot capture/export services
are not registered by this profile.

The option defaults to absent/false. An explicitly malformed or empty value
refuses startup. Reopening requires an explicit false value or removing the option
and restarting the process with its separately verified normal configuration.
Changing a configuration file does not open the running closed process. No header,
query parameter, administrator bearer or health request can open it.

For a disposable image check, use the image's normal API entry point, its normal
listen-port configuration and this environment option. No database or provider
credentials are required. Opening an existing service or moving robot traffic
still needs its own reviewed configuration, identity and recovery plan.

Maintenance mode is a closed installation profile. It does not verify robot
authorization, offer an enrollment/recovery exemption, drain previously running
connections, convert a robot, or establish that the normal runtime is ready.
Entering it restarts the process and interrupts existing robot service; plan that
change with the operator. This source change performs no deployment or restart.
