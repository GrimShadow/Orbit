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

    public const string ContentGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON folders, vocabularies, terms TO dam_app;
        GRANT SELECT, INSERT, UPDATE ON metadata_schemas TO dam_app;   -- versions are never deleted
        -- Subtree queries are prefix searches on the path.
        CREATE INDEX ix_folders_path_prefix ON folders (tenant_id, path text_pattern_ops);
        CREATE INDEX ix_terms_path_prefix ON terms (tenant_id, vocabulary_id, path text_pattern_ops);
        """;

    public const string AssetGrants = """
        GRANT SELECT, INSERT, UPDATE ON assets, uploads TO dam_app;   -- assets are soft-deleted; nothing is ever hard-deleted by the app
        """;

    public const string VersionGrants = """
        GRANT SELECT, INSERT ON asset_versions TO dam_app;   -- versions are immutable: never updated or deleted
        """;

    public const string RenditionGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON renditions TO dam_app;   -- a cache: rows may be replaced or dropped and regenerated
        """;

    public const string RetentionGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON retention_policies TO dam_app;
        """;

    /// <summary>
    /// The publish/expire and retention scheduled jobs (Step 1.6b) find candidates across every tenant, so they read
    /// through the system connection like the outbox relay does — never bypassing RLS outright (dam_system stays
    /// NOBYPASSRLS), just adding a read-only policy for it alongside the ordinary tenant_isolation policy every
    /// tenant-scoped table already has. The jobs still apply each change through the ordinary tenant-scoped connection.
    /// </summary>
    public const string SchedulerSystemGrants = """
        GRANT SELECT ON assets, retention_policies TO dam_system;
        CREATE POLICY system_read_assets ON assets FOR SELECT TO dam_system USING (true);
        CREATE POLICY system_read_retention_policies ON retention_policies FOR SELECT TO dam_system USING (true);
        """;

    /// <summary>The search reindex job (Step 1.7) lists every tenant to rebuild each one's index in turn, the same way the
    /// publish/expire and retention jobs already read across tenants through the system connection.</summary>
    public const string SearchSystemGrants = """
        GRANT SELECT ON tenants TO dam_system;
        CREATE POLICY system_read_tenants ON tenants FOR SELECT TO dam_system USING (true);
        """;

    public const string CollectionGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON collections TO dam_app;
        """;

    /// <summary>Usage events are append-only; the per-asset summary is upserted. Neither is ever deleted by the app.</summary>
    public const string UsageGrants = """
        GRANT SELECT, INSERT ON usage_events TO dam_app;
        GRANT SELECT, INSERT, UPDATE ON asset_usage TO dam_app;
        """;

    /// <summary>Definitions and bindings are edited freely (and a definition may be deleted); instances and tasks are history and
    /// are never deleted by the app.</summary>
    public const string WorkflowGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON workflow_definitions, workflow_bindings TO dam_app;
        GRANT SELECT, INSERT, UPDATE ON workflow_instances, workflow_tasks TO dam_app;
        """;

    /// <summary>The workflow SLA job (Step 2.3) finds overdue tasks across every tenant through the system connection, read-only,
    /// the same way the publish/expire and retention jobs find assets; it then marks each breach through a tenant-scoped scope.</summary>
    public const string WorkflowSystemGrants = """
        GRANT SELECT ON workflow_tasks TO dam_system;
        CREATE POLICY system_read_workflow_tasks ON workflow_tasks FOR SELECT TO dam_system USING (true);
        """;

    /// <summary>Webhooks and chat channels are edited and deleted freely; delivery history, notifications and the dedupe ledger are never
    /// deleted by the app (a webhook's deliveries go with it through the foreign key's cascade).</summary>
    public const string NotificationGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON webhooks, notification_channels TO dam_app;
        GRANT SELECT, INSERT, UPDATE ON webhook_deliveries, notifications, notification_preferences, siem_cursors TO dam_app;
        GRANT SELECT, INSERT ON notification_deliveries TO dam_app;
        """;

    /// <summary>The webhook retry job (Step 2.6) finds due deliveries across every tenant through the system connection, read-only, like the
    /// workflow SLA job; each attempt is then made through a tenant-scoped scope.</summary>
    public const string WebhookSystemGrants = """
        GRANT SELECT ON webhook_deliveries TO dam_system;
        CREATE POLICY system_read_webhook_deliveries ON webhook_deliveries FOR SELECT TO dam_system USING (true);
        """;

    /// <summary>Comments are never deleted by the app (a delete erases the text and sets deleted_at); a comment's mention rows are replaced when its
    /// text is edited and removed when it is deleted.</summary>
    public const string CommentGrants = """
        GRANT SELECT, INSERT, UPDATE ON comments TO dam_app;
        GRANT SELECT, INSERT, DELETE ON comment_mentions TO dam_app;
        """;

    /// <summary>Rights records are edited and deleted (never while assigned); documents are added and removed; the alert ledger is append-only
    /// (its rows go with their rights record or asset through the foreign keys, which need no grant). Names are unique per tenant, ignoring case.</summary>
    public const string RightsGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON rights TO dam_app;
        GRANT SELECT, INSERT, DELETE ON rights_documents TO dam_app;
        GRANT SELECT, INSERT ON rights_alerts TO dam_app;
        CREATE UNIQUE INDEX ux_rights_tenant_lower_name ON rights (tenant_id, lower(name));
        """;

    /// <summary>The rights job (Step 2.5) finds licences ending soon or already ended across every tenant through the system connection, read-only,
    /// like the other cross-tenant jobs (assets are already readable that way); alerts and expiries are then applied through a tenant-scoped scope.</summary>
    public const string RightsSystemGrants = """
        GRANT SELECT ON rights TO dam_system;
        CREATE POLICY system_read_rights ON rights FOR SELECT TO dam_system USING (true);
        """;

    /// <summary>AI settings are edited in place; suggestions, extracts, embeddings and runs are rewritten by re-runs and deleted with an asset's derived data
    /// when it is purged (their rows also go with the asset through the foreign keys, which need no grant).</summary>
    public const string AiGrants = """
        GRANT SELECT, INSERT, UPDATE ON ai_settings TO dam_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ai_suggestions, asset_extracts, asset_embeddings, ai_runs TO dam_app;
        """;

    /// <summary>The AI catch-up job (Step 2.1) finds the tenants that have AI switched on through the system connection, read-only, like the other cross-tenant jobs.</summary>
    public const string AiSystemGrants = """
        GRANT SELECT ON ai_settings TO dam_system;
        CREATE POLICY system_read_ai_settings ON ai_settings FOR SELECT TO dam_system USING (true);
        """;

    /// <summary>Glossary terms are edited and deleted freely. Translation jobs are history: created, updated by the worker as they run, never deleted by the app
    /// (they go with their asset through the foreign key, which needs no grant).</summary>
    public const string TranslationGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON glossary_terms TO dam_app;
        GRANT SELECT, INSERT, UPDATE ON translation_jobs TO dam_app;
        """;

    /// <summary>Rollups, dirty marks and event keys (ADR-0037). The app role rebuilds a tenant-day by deleting and re-inserting its rollup rows, and clears the dirty
    /// mark it has covered; event keys are append-only (retention prunes them through a function, never the app role). Schedules are edited and deleted freely.</summary>
    public const string AnalyticsGrants = """
        GRANT SELECT, INSERT, DELETE ON usage_daily TO dam_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON usage_rollup_dirty TO dam_app;
        GRANT USAGE ON SEQUENCE usage_rollup_mark_seq TO dam_app;
        GRANT SELECT, INSERT ON usage_event_keys TO dam_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON report_schedules TO dam_app;
        """;

    /// <summary>The rollup job finds tenant-days to roll up, and the report job finds due schedules, across every tenant through the system connection, read-only,
    /// like the other cross-tenant jobs; the work itself is then done through a tenant-scoped scope. The one write: retention prunes old event keys (and only those).</summary>
    public const string AnalyticsSystemGrants = """
        GRANT SELECT ON usage_rollup_dirty, report_schedules TO dam_system;
        CREATE POLICY system_read_usage_rollup_dirty ON usage_rollup_dirty FOR SELECT TO dam_system USING (true);
        CREATE POLICY system_read_report_schedules ON report_schedules FOR SELECT TO dam_system USING (true);
        GRANT SELECT, DELETE ON usage_event_keys TO dam_system;
        CREATE POLICY system_read_usage_event_keys ON usage_event_keys FOR SELECT TO dam_system USING (true);
        CREATE POLICY system_prune_usage_event_keys ON usage_event_keys FOR DELETE TO dam_system USING (true);
        """;

    /// <summary>Connections are edited and deleted freely. Calls are history: written by connector runs, trimmed to the latest 500 per connection by the daily cleanup (through the tenant-scoped role),
    /// and removed with their connection through the foreign key. Connection names are unique per tenant, ignoring case.</summary>
    public const string IntegrationGrants = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON integration_connections TO dam_app;
        GRANT SELECT, INSERT, DELETE ON integration_calls TO dam_app;
        CREATE UNIQUE INDEX ux_integration_connections_tenant_lower_name ON integration_connections (tenant_id, lower(name));
        """;

    /// <summary>The cleanup job (ADR-0040) finds the connections whose activity log has outgrown its limit across every tenant through the system connection, read-only, like the other
    /// cross-tenant jobs; the trimming itself is done through a tenant-scoped scope.</summary>
    public const string IntegrationSystemGrants = """
        GRANT SELECT ON integration_calls TO dam_system;
        CREATE POLICY system_read_integration_calls ON integration_calls FOR SELECT TO dam_system USING (true);
        """;
}
