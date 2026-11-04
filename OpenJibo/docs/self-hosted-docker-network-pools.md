# Recover a new restore project when Docker address pools are exhausted

Many retained Compose project networks can exhaust Docker's automatic address
pools. Docker then refuses a new network with `all predefined address pools have
been fully subnetted`. Reinitializing databases, keys or volumes does not solve
this host networking limit.

During integration-transfer preview acceptance, the AVX2 restore project hit this
limit before PostgreSQL started. Its original verified backup and installation
were retained. The restore resumed on a new explicit subnet after comparing it
with all existing Docker network ranges and IPv4 routes on the actual daemon host.
No older networks were pruned, and Docker daemon configuration was unchanged.
The source installation's keys, data and backup stayed intact.

## Scope the workaround to the new restore project

Keep the failed output and verify the original backup again. Confirm the source
writers are stopped and identify the intended new restore project, its volumes,
environment file and original Compose files. Inspect existing Docker networks
and routes on the daemon host, including VPN routes. A client machine's routes
are insufficient when Docker uses a different host or VM.

For example, these commands inspect native Linux daemon-host ranges:

```text
docker network ls
docker network inspect NETWORK_ID
ip -j -4 route show table all
```

Choose a private subnet that overlaps neither those routes nor any existing
Docker subnet. The acceptance host used `10.253.200.0/24` after checking its own
ranges; that is a recorded test value, not a default for another installation.

Create a separate Compose override for the new restore project. Replace the
placeholder with the subnet checked for that host:

```yaml
networks:
  default:
    ipam:
      config:
        - subnet: CHOOSE_UNUSED_PRIVATE_SUBNET
```

Append this override with `-f` after the installation's existing Compose files
when creating the new restore project's network. Keep the original environment,
image digest and other Compose options. This changes only that new network's
allocation; it does not require deleting retained networks or changing the
Docker daemon's global address pools. Do not apply a new allocation to an
existing live project's network as part of this recovery procedure.

Resume restoration from the verified backup, then repeat health, identity,
ownership and integration checks. Retain the original failure, the range check
and the resumed results so a successful retry is not reported as a first-attempt
installation. Follow the [recovery guide](self-hosted-recovery.md) for the data
restore itself.
