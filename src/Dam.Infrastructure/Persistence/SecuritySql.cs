namespace Dam.Infrastructure.Persistence;

/// <summary>Raw SQL used by migrations. Future tenant-scoped tables must call <see cref="EnableTenantRls"/>.</summary>
public static class SecuritySql
{
    public const string TenantPredicate =
        "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

    public static string EnableTenantRls(string table) => $"""
        ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON {table}
            USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});
        """;

    public const string AppRole = """
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dam_app') THEN
                CREATE ROLE dam_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
            END IF;
        END $$;
        GRANT USAGE ON SCHEMA public TO dam_app;
        """;

    /// <summary>Hash-chained, append-only audit log (spec A7.10). One chain per tenant.</summary>
    public const string AuditChain = """
        CREATE FUNCTION audit_row_hash(prev text, a audit_log) RETURNS text
        LANGUAGE sql IMMUTABLE AS $f$
            SELECT encode(sha256(convert_to(concat_ws('|',
                prev, a.id, a.tenant_id, a.chain_no,
                to_char(a.ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
                a.actor, a.action, a.entity_type, a.entity_id,
                a.before::text, a.after::text, a.ip), 'UTF8')), 'hex')
        $f$;

        CREATE FUNCTION audit_log_chain() RETURNS trigger LANGUAGE plpgsql AS $f$
        DECLARE last_row audit_log;
        BEGIN
            -- Serialise appends per tenant so chain_no is gap-free and ordered.
            PERFORM pg_advisory_xact_lock(hashtextextended(NEW.tenant_id::text, 0));
            SELECT * INTO last_row FROM audit_log
                WHERE tenant_id = NEW.tenant_id ORDER BY chain_no DESC LIMIT 1;
            NEW.chain_no := coalesce(last_row.chain_no, 0) + 1;
            NEW.prev_hash := coalesce(last_row.hash, '');
            NEW.hash := audit_row_hash(NEW.prev_hash, NEW);
            RETURN NEW;
        END $f$;

        CREATE TRIGGER audit_log_chain_bi BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION audit_log_chain();

        CREATE FUNCTION audit_log_immutable() RETURNS trigger LANGUAGE plpgsql AS $f$
        BEGIN
            RAISE EXCEPTION 'audit_log is append-only';
        END $f$;

        CREATE TRIGGER audit_log_no_update BEFORE UPDATE OR DELETE ON audit_log
            FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
        CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log
            FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

        -- Returns NULL when the tenant's chain is intact, else the first broken chain_no.
        CREATE FUNCTION audit_verify(p_tenant uuid) RETURNS bigint LANGUAGE plpgsql STABLE AS $f$
        DECLARE r audit_log; expected_prev text := ''; expected_no bigint := 1;
        BEGIN
            FOR r IN SELECT * FROM audit_log WHERE tenant_id = p_tenant ORDER BY chain_no LOOP
                IF r.chain_no <> expected_no OR r.prev_hash <> expected_prev
                   OR r.hash <> audit_row_hash(r.prev_hash, r) THEN
                    RETURN r.chain_no;
                END IF;
                expected_prev := r.hash; expected_no := expected_no + 1;
            END LOOP;
            RETURN NULL;
        END $f$;
        """;

    public const string Grants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON outbox TO dam_app;
        GRANT SELECT, INSERT ON audit_log TO dam_app;
        GRANT EXECUTE ON FUNCTION audit_verify(uuid) TO dam_app;
        """;

    /// <summary>
    /// Restricted role for the outbox relay: may read/mark outbox rows across tenants, touches nothing else.
    /// (RLS is still forced; this role gets its own permissive policy on the one table.)
    /// </summary>
    public const string SystemRelayRole = """
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dam_system') THEN
                CREATE ROLE dam_system NOLOGIN NOSUPERUSER NOBYPASSRLS;
            END IF;
        END $$;
        GRANT USAGE ON SCHEMA public TO dam_system;
        GRANT SELECT, UPDATE ON outbox TO dam_system;
        CREATE POLICY system_relay ON outbox TO dam_system USING (true) WITH CHECK (true);
        GRANT SELECT, INSERT ON processed_messages TO dam_app;
        """;

    /// <summary>The tenants table has no tenant_id: a session may see and change only the row whose id is its tenant.</summary>
    public const string TenantsRls = """
        ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
        ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_self ON tenants
            USING (id = nullif(current_setting('app.tenant_id', true), '')::uuid)
            WITH CHECK (id = nullif(current_setting('app.tenant_id', true), '')::uuid);
        """;

    public const string IdentityGrants = """
        GRANT SELECT, INSERT, UPDATE ON tenants, users TO dam_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON groups, roles, access_rules, user_groups, user_roles, group_roles TO dam_app;
        -- Names are unique per tenant, ignoring case.
        CREATE UNIQUE INDEX ux_groups_tenant_lower_name ON groups (tenant_id, lower(name));
        CREATE UNIQUE INDEX ux_roles_tenant_lower_name ON roles (tenant_id, lower(name));
        """;
}
