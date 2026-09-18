#!/usr/bin/env bash
set -euo pipefail

readonly expected_resource_group="rg-openjibo-staging"
readonly expected_database="openjibo_cloud"
readonly postgres_admin="openjiboadmin"
readonly admin_secret_name="openjibo-postgres-admin-password"
readonly deployer_secret_name="openjibo-cloud-postgresql-deployer-password"
readonly deployer_role="openjibo_cloud_staging_deployer"
readonly metering_role="openjibo_usage_metering_runtime"
readonly reconciliation_role="openjibo_usage_reconciliation_runtime"

if [[ "${OPENJIBO_BOOTSTRAP_CONFIRMED:-}" != "true" ]]; then
  echo "The staging PostgreSQL bootstrap requires explicit confirmation." >&2
  exit 1
fi
if [[ "${OPENJIBO_RESOURCE_GROUP:-}" != "$expected_resource_group" ]]; then
  echo "The staging PostgreSQL bootstrap is bound to rg-openjibo-staging." >&2
  exit 1
fi
for command_name in az curl jq openssl psql; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command '$command_name' was not found." >&2
    exit 1
  fi
done

subscription_id="$(az account show --query id --output tsv --only-show-errors)"
if [[ ! "$subscription_id" =~ ^[0-9a-fA-F-]{36}$ ]]; then
  echo "The active Azure subscription identity is missing or invalid." >&2
  exit 1
fi

mapfile -t postgres_rows < <(az postgres flexible-server list \
  --resource-group "$expected_resource_group" \
  --query "[?tags.openjiboEnvironment=='staging'].[name,fullyQualifiedDomainName,id]" \
  --output tsv --only-show-errors)
if (( ${#postgres_rows[@]} != 1 )); then
  echo "Expected exactly one tagged staging PostgreSQL server." >&2
  exit 1
fi
IFS=$'\t' read -r postgres_server postgres_host postgres_resource_id <<<"${postgres_rows[0]}"

mapfile -t vault_rows < <(az keyvault list \
  --resource-group "$expected_resource_group" \
  --query "[?tags.openjiboEnvironment=='staging'].[name,id]" \
  --output tsv --only-show-errors)
if (( ${#vault_rows[@]} != 1 )); then
  echo "Expected exactly one tagged staging Key Vault." >&2
  exit 1
fi
IFS=$'\t' read -r key_vault_name vault_resource_id <<<"${vault_rows[0]}"

expected_resource_prefix="/subscriptions/${subscription_id,,}/resourcegroups/${expected_resource_group}/"
if [[ "${postgres_resource_id,,}" != "$expected_resource_prefix"* ||
      "${vault_resource_id,,}" != "$expected_resource_prefix"* ||
      ! "$postgres_host" =~ ^[a-z0-9][a-z0-9-]{1,61}\.postgres\.database\.azure\.com$ ]]; then
  echo "Resolved Azure resources are outside the protected staging boundary." >&2
  exit 1
fi

database_count="$(az postgres flexible-server db list \
  --resource-group "$expected_resource_group" \
  --server-name "$postgres_server" \
  --query "[?name=='${expected_database}'] | length(@)" \
  --output tsv --only-show-errors)"
if [[ "$database_count" == "0" ]]; then
  az postgres flexible-server db create \
    --resource-group "$expected_resource_group" \
    --server-name "$postgres_server" \
    --name "$expected_database" \
    --charset UTF8 \
    --collation en_US.utf8 \
    --only-show-errors >/dev/null
elif [[ "$database_count" != "1" ]]; then
  echo "The isolated staging database lookup returned an invalid result." >&2
  exit 1
fi
database_id="$(az postgres flexible-server db show \
  --resource-group "$expected_resource_group" \
  --server-name "$postgres_server" \
  --name "$expected_database" \
  --query id --output tsv --only-show-errors)"
if [[ "${database_id,,}" != "${postgres_resource_id,,}/databases/${expected_database}" ]]; then
  echo "The isolated openjibo_cloud staging database was not found on the protected server." >&2
  exit 1
fi

work_dir="$(mktemp -d)"
admin_pgpass="$work_dir/admin.pgpass"
deployer_pgpass="$work_dir/deployer.pgpass"
bootstrap_sql="$work_dir/bootstrap.sql"
run_identity="${GITHUB_RUN_ID:-local-$(openssl rand -hex 6)}"
firewall_rule="usage-bootstrap-${run_identity}-${GITHUB_RUN_ATTEMPT:-1}"
firewall_created=false
cleanup() {
  unset PGPASSFILE
  if [[ "$firewall_created" == "true" ]]; then
    az postgres flexible-server firewall-rule delete \
      --resource-group "$expected_resource_group" \
      --server-name "$postgres_server" \
      --name "$firewall_rule" \
      --yes --only-show-errors >/dev/null || true
  fi
  rm -rf -- "$work_dir"
}
trap cleanup EXIT
umask 077

admin_secret_json="$(az keyvault secret show \
  --vault-name "$key_vault_name" \
  --name "$admin_secret_name" \
  --query '{value:value,enabled:attributes.enabled}' --output json --only-show-errors)"
admin_password="$(jq -r '.value // empty' <<<"$admin_secret_json")"
admin_secret_enabled="$(jq -r '.enabled // false' <<<"$admin_secret_json")"
unset admin_secret_json
if [[ ! "$admin_password" =~ ^[[:graph:]]{1,512}$ || "$admin_secret_enabled" != "true" ]]; then
  echo "The protected PostgreSQL administrator secret is missing or malformed." >&2
  exit 1
fi
unset admin_secret_enabled
echo "::add-mask::$admin_password"

existing_deployer_secret_count="$(az keyvault secret list \
  --vault-name "$key_vault_name" \
  --query "[?name=='${deployer_secret_name}'] | length(@)" \
  --output tsv --only-show-errors)"
store_deployer_secret=false
if [[ "$existing_deployer_secret_count" == "0" ]]; then
  deployer_password="$(openssl rand -base64 48 | tr -d '\r\n')"
  store_deployer_secret=true
elif [[ "$existing_deployer_secret_count" == "1" ]]; then
  deployer_secret_json="$(az keyvault secret show \
    --vault-name "$key_vault_name" \
    --name "$deployer_secret_name" \
    --query '{value:value,enabled:attributes.enabled}' --output json --only-show-errors)"
  deployer_password="$(jq -r '.value // empty' <<<"$deployer_secret_json")"
  deployer_secret_enabled="$(jq -r '.enabled // false' <<<"$deployer_secret_json")"
  unset deployer_secret_json
  if [[ "$deployer_secret_enabled" != "true" ]]; then
    echo "The existing deployer credential is disabled." >&2
    exit 1
  fi
  unset deployer_secret_enabled
else
  echo "The deployer secret lookup returned an invalid result." >&2
  exit 1
fi
if [[ ! "$deployer_password" =~ ^[A-Za-z0-9+/=]{32,128}$ ]]; then
  echo "The deployer credential is missing or malformed." >&2
  exit 1
fi
echo "::add-mask::$deployer_password"

escape_pgpass() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//:/\\:}"
  printf '%s' "$value"
}
printf '%s:5432:%s:%s:%s\n' \
  "$postgres_host" "$expected_database" "$postgres_admin" "$(escape_pgpass "$admin_password")" \
  >"$admin_pgpass"
printf '%s:5432:%s:%s:%s\n' \
  "$postgres_host" "$expected_database" "$deployer_role" "$(escape_pgpass "$deployer_password")" \
  >"$deployer_pgpass"

runner_ip="$(curl -fsS --max-time 15 https://api.ipify.org)"
if [[ ! "$runner_ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
  echo "Could not resolve a valid deployment runner IPv4 address." >&2
  exit 1
fi
az postgres flexible-server firewall-rule create \
  --resource-group "$expected_resource_group" \
  --server-name "$postgres_server" \
  --name "$firewall_rule" \
  --start-ip-address "$runner_ip" \
  --end-ip-address "$runner_ip" \
  --only-show-errors >/dev/null
firewall_created=true

cat >"$bootstrap_sql" <<SQL
BEGIN;
SELECT pg_advisory_xact_lock(hashtextextended('openjibo-cloud-staging-postgres-bootstrap', 0));

DO \$bootstrap\$
DECLARE
  unsafe_count integer;
  unauthorized_members text;
BEGIN
  SELECT count(*) INTO unsafe_count
  FROM pg_roles
  WHERE rolname = '${deployer_role}'
    AND (NOT rolcanlogin OR NOT rolinherit OR rolsuper OR rolcreatedb OR
         rolcreaterole OR rolreplication OR rolbypassrls);
  IF unsafe_count <> 0 THEN
    RAISE EXCEPTION 'existing OpenJiboCloud deployer attributes are unsafe';
  END IF;

  SELECT count(*) INTO unsafe_count
  FROM pg_roles
  WHERE rolname IN ('${metering_role}', '${reconciliation_role}')
    AND (rolcanlogin OR NOT rolinherit OR rolsuper OR rolcreatedb OR
         rolcreaterole OR rolreplication OR rolbypassrls);
  IF unsafe_count <> 0 THEN
    RAISE EXCEPTION 'existing OpenJiboCloud runtime principal attributes are unsafe';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${deployer_role}') THEN
    CREATE ROLE ${deployer_role} LOGIN INHERIT NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${metering_role}') THEN
    CREATE ROLE ${metering_role} NOLOGIN INHERIT NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${reconciliation_role}') THEN
    CREATE ROLE ${reconciliation_role} NOLOGIN INHERIT NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_checkpoint_owner') THEN
    CREATE ROLE openjibo_usage_checkpoint_owner NOLOGIN NOINHERIT NOSUPERUSER
      NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_metering') THEN
    CREATE ROLE openjibo_usage_metering NOLOGIN INHERIT NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_reconciler') THEN
    CREATE ROLE openjibo_usage_reconciler NOLOGIN INHERIT NOSUPERUSER NOCREATEDB
      NOCREATEROLE NOREPLICATION NOBYPASSRLS;
  END IF;

  SELECT count(*) INTO unsafe_count
  FROM pg_roles
  WHERE rolname IN ('openjibo_usage_checkpoint_owner', 'openjibo_usage_metering',
                    'openjibo_usage_reconciler')
    AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR
         rolreplication OR rolbypassrls);
  IF unsafe_count <> 0 OR
     NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_checkpoint_owner' AND NOT rolinherit) OR
     NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_metering' AND rolinherit) OR
     NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'openjibo_usage_reconciler' AND rolinherit) THEN
    RAISE EXCEPTION 'existing usage capability role attributes are unsafe';
  END IF;

  IF EXISTS (
       SELECT 1
       FROM pg_auth_members membership
       JOIN pg_roles member_role ON member_role.oid = membership.member
       JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
       WHERE member_role.rolname = '${deployer_role}'
         AND granted_role.rolname NOT IN (
           'openjibo_usage_checkpoint_owner', 'openjibo_usage_metering',
           'openjibo_usage_reconciler')) OR
     EXISTS (
       SELECT 1
       FROM pg_auth_members membership
       JOIN pg_roles member_role ON member_role.oid = membership.member
       JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
       WHERE member_role.rolname = '${metering_role}'
         AND granted_role.rolname <> 'openjibo_usage_metering') OR
     EXISTS (
       SELECT 1
       FROM pg_auth_members membership
       JOIN pg_roles member_role ON member_role.oid = membership.member
       JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
       WHERE member_role.rolname = '${reconciliation_role}'
         AND granted_role.rolname <> 'openjibo_usage_reconciler') OR
     EXISTS (
       SELECT 1
       FROM pg_auth_members membership
       JOIN pg_roles member_role ON member_role.oid = membership.member
       WHERE member_role.rolname IN (
         'openjibo_usage_checkpoint_owner', 'openjibo_usage_metering',
         'openjibo_usage_reconciler')) THEN
    RAISE EXCEPTION 'existing OpenJiboCloud role membership is unsafe';
  END IF;

  IF EXISTS (
       SELECT 1
       FROM pg_auth_members membership
       JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
       JOIN pg_roles member_role ON member_role.oid = membership.member
       WHERE granted_role.rolname IN (
           'openjibo_usage_checkpoint_owner', 'openjibo_usage_metering',
           'openjibo_usage_reconciler')
         AND NOT (
           (member_role.rolname = '${deployer_role}' AND membership.admin_option) OR
           (granted_role.rolname = 'openjibo_usage_metering' AND
            member_role.rolname = '${metering_role}' AND
            NOT membership.admin_option AND membership.inherit_option AND
            NOT membership.set_option) OR
           (granted_role.rolname = 'openjibo_usage_reconciler' AND
            member_role.rolname = '${reconciliation_role}' AND
            NOT membership.admin_option AND membership.inherit_option AND
            NOT membership.set_option)
         )) THEN
    SELECT string_agg(
             format('%s->%s(admin=%s,inherit=%s,set=%s)',
               granted_role.rolname, member_role.rolname,
               membership.admin_option, membership.inherit_option,
               membership.set_option), ', ' ORDER BY granted_role.rolname, member_role.rolname)
    INTO unauthorized_members
    FROM pg_auth_members membership
    JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
    JOIN pg_roles member_role ON member_role.oid = membership.member
    WHERE granted_role.rolname IN (
        'openjibo_usage_checkpoint_owner', 'openjibo_usage_metering',
        'openjibo_usage_reconciler')
      AND NOT (
        (member_role.rolname = '${deployer_role}' AND membership.admin_option) OR
        (granted_role.rolname = 'openjibo_usage_metering' AND
         member_role.rolname = '${metering_role}' AND
         NOT membership.admin_option AND membership.inherit_option AND
         NOT membership.set_option) OR
        (granted_role.rolname = 'openjibo_usage_reconciler' AND
         member_role.rolname = '${reconciliation_role}' AND
         NOT membership.admin_option AND membership.inherit_option AND
         NOT membership.set_option)
      );
    RAISE EXCEPTION 'usage capability role has an unauthorized member: %', unauthorized_members;
  END IF;
END
\$bootstrap\$;

ALTER ROLE ${deployer_role} PASSWORD '${deployer_password}';
REVOKE ALL ON DATABASE ${expected_database} FROM PUBLIC;
GRANT CONNECT, TEMPORARY, CREATE ON DATABASE ${expected_database} TO ${deployer_role};
GRANT openjibo_usage_checkpoint_owner, openjibo_usage_metering,
      openjibo_usage_reconciler TO ${deployer_role} WITH ADMIN OPTION;
COMMIT;
SQL

admin_connection="host=$postgres_host port=5432 dbname=$expected_database user=$postgres_admin sslmode=verify-full sslrootcert=system connect_timeout=15"
PGPASSFILE="$admin_pgpass" psql "$admin_connection" --no-psqlrc \
  --set ON_ERROR_STOP=1 --file "$bootstrap_sql" >/dev/null

if [[ "$store_deployer_secret" == "true" ]]; then
  az keyvault secret set \
    --vault-name "$key_vault_name" \
    --name "$deployer_secret_name" \
    --value "$deployer_password" \
    --only-show-errors >/dev/null
fi
unset admin_password deployer_password

deployer_connection="host=$postgres_host port=5432 dbname=$expected_database user=$deployer_role sslmode=verify-full sslrootcert=system connect_timeout=15"
identity="$(PGPASSFILE="$deployer_pgpass" psql "$deployer_connection" --no-psqlrc \
  --tuples-only --no-align --field-separator '|' --set ON_ERROR_STOP=1 \
  --command "SELECT current_database(), current_user, COALESCE((SELECT ssl::text FROM pg_stat_ssl WHERE pid=pg_backend_pid()), 'false');")"
IFS='|' read -r actual_database actual_user ssl_enabled <<<"$identity"
if [[ "$actual_database" != "$expected_database" || "$actual_user" != "$deployer_role" ||
      "$ssl_enabled" != "true" ]]; then
  echo "The dedicated deployer could not authenticate to the protected TLS staging database." >&2
  exit 1
fi

echo "OpenJiboCloud staging PostgreSQL principals and deployer credential are ready."
