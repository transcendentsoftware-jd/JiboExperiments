using Npgsql;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>
/// Provisions the fixed least-privilege PostgreSQL roles used by the replay observer.
/// This is an administrative deployment operation and must never run in the API host.
/// </summary>
public static class PostgreSqlAwsSigV4ReplayRoleProvisioner
{
    public const string OwnerRole = "openjibo_sigv4_replay_owner";
    public const string CapabilityRole = "openjibo_sigv4_replay_runtime";
    public const string LoginRole = "openjibo_sigv4_replay_observer";

    public static async Task ProvisionAsync(
        string administratorConnectionString,
        string observerConnectionString,
        CancellationToken cancellationToken = default)
    {
        var administrator = Parse(administratorConnectionString, nameof(administratorConnectionString));
        var observer = Parse(observerConnectionString, nameof(observerConnectionString));

        if (!string.Equals(observer.Username, LoginRole, StringComparison.Ordinal))
            throw new ArgumentException($"Replay observer username must be '{LoginRole}'.",
                nameof(observerConnectionString));
        if (string.IsNullOrWhiteSpace(observer.Password))
            throw new ArgumentException("Replay observer password is required.", nameof(observerConnectionString));
        if (observer.SslMode < SslMode.Require && !IsLoopbackHost(observer.Host))
            throw new ArgumentException("Replay observer connection must require TLS.",
                nameof(observerConnectionString));
        if (!SameServerAndDatabase(administrator, observer))
            throw new ArgumentException(
                "Replay observer and administrator connections must target the same PostgreSQL server and database.",
                nameof(observerConnectionString));

        await using var connection = new NpgsqlConnection(administrator.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var quotedPassword = await QuoteLiteralAsync(connection, transaction, observer.Password, cancellationToken);
        var quotedDatabase = QuoteIdentifier(administrator.Database
                                             ?? throw new ArgumentException(
                                                 "Administrator database name is required.",
                                                 nameof(administratorConnectionString)));
        var sql = $$"""
                    DO $roles$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{{OwnerRole}}') THEN
                            CREATE ROLE {{OwnerRole}} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{{CapabilityRole}}') THEN
                            CREATE ROLE {{CapabilityRole}} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{{LoginRole}}') THEN
                            CREATE ROLE {{LoginRole}} LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 2;
                        END IF;
                    END
                    $roles$;

                    ALTER ROLE {{OwnerRole}} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                    ALTER ROLE {{CapabilityRole}} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                    ALTER ROLE {{LoginRole}} LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 2 PASSWORD {{quotedPassword}};

                    GRANT {{OwnerRole}} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE, ADMIN FALSE;
                    -- PostgreSQL requires a function's new owner to hold CREATE on the
                    -- containing schema. Azure revokes PUBLIC CREATE on public, unlike
                    -- many local test installations, so grant it only for the ownership
                    -- transfer and revoke it immediately afterward.
                    GRANT USAGE, CREATE ON SCHEMA public TO {{OwnerRole}};
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.AwsSigV4ReplayObservations TO {{OwnerRole}};
                    REVOKE ALL ON TABLE public.AwsSigV4ReplayObservations FROM PUBLIC, {{CapabilityRole}}, {{LoginRole}};
                    REVOKE {{OwnerRole}} FROM {{LoginRole}};
                    GRANT CONNECT ON DATABASE {{quotedDatabase}} TO {{CapabilityRole}};
                    GRANT USAGE ON SCHEMA public TO {{CapabilityRole}};
                    GRANT {{CapabilityRole}} TO {{LoginRole}} WITH INHERIT TRUE, SET FALSE, ADMIN FALSE;

                    DO $ownership$
                    BEGIN
                        IF pg_catalog.pg_get_userbyid((
                            SELECT proowner
                            FROM pg_catalog.pg_proc
                            WHERE oid = 'public.ObserveAwsSigV4Replay(bytea,smallint,text)'::pg_catalog.regprocedure
                        )) <> '{{OwnerRole}}' THEN
                            ALTER FUNCTION public.ObserveAwsSigV4Replay(BYTEA, SMALLINT, TEXT) OWNER TO {{OwnerRole}};
                        END IF;
                    END
                    $ownership$;
                    REVOKE CREATE ON SCHEMA public FROM {{OwnerRole}};

                    SET LOCAL ROLE {{OwnerRole}};
                    DO $acl$
                    DECLARE
                        grantee_name TEXT;
                    BEGIN
                        FOR grantee_name IN
                            SELECT role_name.rolname
                            FROM pg_catalog.pg_proc procedure
                            CROSS JOIN LATERAL pg_catalog.aclexplode(
                                COALESCE(procedure.proacl, pg_catalog.acldefault('f', procedure.proowner))
                            ) acl
                            JOIN pg_catalog.pg_roles role_name ON role_name.oid = acl.grantee
                            WHERE procedure.oid = 'public.ObserveAwsSigV4Replay(bytea,smallint,text)'::pg_catalog.regprocedure
                              AND acl.privilege_type = 'EXECUTE'
                              AND role_name.rolname NOT IN ('{{OwnerRole}}', '{{CapabilityRole}}')
                        LOOP
                            EXECUTE pg_catalog.format(
                                'REVOKE EXECUTE ON FUNCTION public.ObserveAwsSigV4Replay(bytea,smallint,text) FROM %I',
                                grantee_name
                            );
                        END LOOP;
                    END
                    $acl$;
                    REVOKE ALL ON FUNCTION public.ObserveAwsSigV4Replay(BYTEA, SMALLINT, TEXT) FROM PUBLIC, {{LoginRole}};
                    GRANT EXECUTE ON FUNCTION public.ObserveAwsSigV4Replay(BYTEA, SMALLINT, TEXT) TO {{CapabilityRole}};
                    RESET ROLE;

                    DO $verify$
                    BEGIN
                        IF 2 <> (
                            SELECT COUNT(*)
                            FROM pg_catalog.pg_roles
                            WHERE rolname IN ('{{OwnerRole}}', '{{CapabilityRole}}')
                              AND NOT rolcanlogin AND NOT rolinherit AND NOT rolsuper
                              AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
                              AND NOT rolbypassrls
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay non-login role attributes are invalid';
                        END IF;
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_catalog.pg_roles
                            WHERE rolname = '{{LoginRole}}'
                              AND rolcanlogin AND rolinherit AND NOT rolsuper
                              AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
                              AND NOT rolbypassrls AND rolconnlimit = 2
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay observer login attributes are invalid';
                        END IF;
                        IF EXISTS (
                            SELECT 1
                            FROM pg_catalog.pg_auth_members membership
                            JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                            JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                            WHERE member.rolname = '{{LoginRole}}'
                              AND parent.rolname <> '{{CapabilityRole}}'
                        ) OR NOT EXISTS (
                            SELECT 1
                            FROM pg_catalog.pg_auth_members membership
                            JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                            JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                            WHERE member.rolname = '{{LoginRole}}'
                              AND parent.rolname = '{{CapabilityRole}}'
                              AND membership.inherit_option
                              AND NOT membership.set_option
                              AND NOT membership.admin_option
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay observer role membership is invalid';
                        END IF;
                        -- PostgreSQL 16 grants a non-superuser creator ADMIN OPTION on
                        -- roles it creates. Azure PostgreSQL uses that path for its
                        -- deployment administrator, so CURRENT_USER is an expected
                        -- administrative member; no other runtime member is permitted.
                        IF EXISTS (
                            SELECT 1
                            FROM pg_catalog.pg_auth_members membership
                            JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                            WHERE member.rolname IN ('{{OwnerRole}}', '{{CapabilityRole}}')
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay supporting roles must not inherit other roles';
                        END IF;
                        IF EXISTS (
                            SELECT 1
                            FROM pg_catalog.pg_auth_members membership
                            JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                            JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                            WHERE parent.rolname = '{{CapabilityRole}}'
                              AND member.rolname NOT IN ('{{LoginRole}}', CURRENT_USER)
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay capability role has an unexpected member';
                        END IF;
                        IF EXISTS (
                            SELECT 1
                            FROM pg_catalog.pg_auth_members membership
                            JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                            JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                            WHERE parent.rolname = '{{OwnerRole}}'
                              AND member.rolname <> CURRENT_USER
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay owner role has an unexpected member';
                        END IF;
                        IF NOT pg_catalog.has_function_privilege(
                            '{{LoginRole}}',
                            'public.ObserveAwsSigV4Replay(bytea,smallint,text)',
                            'EXECUTE'
                        ) OR pg_catalog.has_table_privilege(
                            '{{LoginRole}}',
                            'public.AwsSigV4ReplayObservations',
                            'SELECT,INSERT,UPDATE,DELETE'
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay observer privileges are invalid';
                        END IF;
                        IF pg_catalog.has_schema_privilege(
                            '{{OwnerRole}}',
                            'public',
                            'CREATE'
                        ) THEN
                            RAISE EXCEPTION 'SigV4 replay owner retained schema CREATE privilege';
                        END IF;
                    END
                    $verify$;
                    """;

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static NpgsqlConnectionStringBuilder Parse(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A PostgreSQL connection string is required.", parameterName);
        return new NpgsqlConnectionStringBuilder(value);
    }

    private static bool SameServerAndDatabase(
        NpgsqlConnectionStringBuilder left,
        NpgsqlConnectionStringBuilder right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        string.Equals(left.Database, right.Database, StringComparison.Ordinal);

    private static async Task<string> QuoteLiteralAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.quote_literal(@value)", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        return (string)(await command.ExecuteScalarAsync(cancellationToken)
                        ?? throw new InvalidOperationException("PostgreSQL did not quote the observer password."));
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsLoopbackHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal);
}
