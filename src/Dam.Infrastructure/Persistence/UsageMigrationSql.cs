namespace Dam.Infrastructure.Persistence;

/// <summary>
/// The raw SQL of the usage rollup migration (ADR-0037): the back-fill of <c>assets.first_published_at</c>, the partition maintenance functions, and
/// the conversion of <c>usage_events</c> into a month-partitioned table with the existing rows copied across. Kept apart from the migration so it
/// reads as SQL.
/// </summary>
public static class UsageMigrationSql
{
    public const string BackfillFirstPublished = """
        UPDATE assets a SET first_published_at = s.first_at
        FROM (SELECT entity_id, min(ts) AS first_at FROM audit_log
              WHERE action = 'asset.status.changed' AND entity_type = 'asset' AND after ->> 'To' = 'published'
              GROUP BY entity_id) s
        WHERE a.id = s.entity_id AND a.first_published_at IS NULL;
        """;

    /// <summary>
    /// The app roles cannot run DDL, so partitions are created and dropped by <c>SECURITY DEFINER</c> functions owned by the migrating role and
    /// executable only by the system role the scheduler uses. Each partition has forced RLS like every other tenant table (a read through the parent
    /// applies the parent's policy; the partition's own protects it from direct access). The default partition catches an event no month covers, so
    /// an event is never lost; creating a month first moves that month's strays out of it. The toggling of FORCE on the default partition is for an
    /// owner that is not a superuser: a forced policy would otherwise hide every row from the function.
    /// </summary>
    public const string Functions = """
        CREATE FUNCTION usage_events_ensure_partition(p_month date) RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $f$
        DECLARE
            v_from date := date_trunc('month', p_month)::date;
            v_to date := (date_trunc('month', p_month) + interval '1 month')::date;
            v_name text := 'usage_events_' || to_char(date_trunc('month', p_month), 'YYYY_MM');
            v_lo text := to_char(date_trunc('month', p_month), 'YYYY-MM-DD') || ' 00:00:00+00';
            v_hi text := to_char(date_trunc('month', p_month) + interval '1 month', 'YYYY-MM-DD') || ' 00:00:00+00';
            v_strays boolean;
            v_predicate text := 'tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid';
        BEGIN
            IF to_regclass(format('public.%I', v_name)) IS NOT NULL THEN RETURN false; END IF;
            PERFORM set_config('lock_timeout', '10s', true);
            PERFORM pg_advisory_xact_lock(hashtextextended('usage_events_partitions', 0));
            IF to_regclass(format('public.%I', v_name)) IS NOT NULL THEN RETURN false; END IF;

            EXECUTE 'ALTER TABLE public.usage_events_default NO FORCE ROW LEVEL SECURITY';
            SELECT EXISTS (SELECT 1 FROM public.usage_events_default WHERE occurred_at >= v_lo::timestamptz AND occurred_at < v_hi::timestamptz) INTO v_strays;
            IF v_strays THEN
                EXECUTE format('CREATE TABLE public.%I (LIKE public.usage_events INCLUDING DEFAULTS INCLUDING INDEXES)', v_name);
                EXECUTE format('WITH moved AS (DELETE FROM public.usage_events_default WHERE occurred_at >= %L::timestamptz AND occurred_at < %L::timestamptz RETURNING *) INSERT INTO public.%I SELECT * FROM moved', v_lo, v_hi, v_name);
                EXECUTE format('ALTER TABLE public.usage_events ATTACH PARTITION public.%I FOR VALUES FROM (%L) TO (%L)', v_name, v_lo, v_hi);
            ELSE
                EXECUTE format('CREATE TABLE public.%I PARTITION OF public.usage_events FOR VALUES FROM (%L) TO (%L)', v_name, v_lo, v_hi);
            END IF;
            EXECUTE 'ALTER TABLE public.usage_events_default FORCE ROW LEVEL SECURITY';

            EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', v_name);
            EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', v_name);
            EXECUTE format('CREATE POLICY tenant_isolation ON public.%I USING (%s) WITH CHECK (%s)', v_name, v_predicate, v_predicate);
            RETURN true;
        END $f$;

        CREATE FUNCTION usage_events_partition_months() RETURNS SETOF date
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $f$
            SELECT to_date(substr(c.relname, 14), 'YYYY_MM')
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'usage_events' AND p.relnamespace = 'public'::regnamespace AND c.relname ~ '^usage_events_[0-9]{4}_[0-9]{2}$'
            ORDER BY 1
        $f$;

        CREATE FUNCTION usage_events_stray_months() RETURNS SETOF date
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $f$
        BEGIN
            EXECUTE 'ALTER TABLE public.usage_events_default NO FORCE ROW LEVEL SECURITY';
            RETURN QUERY SELECT DISTINCT date_trunc('month', occurred_at AT TIME ZONE 'UTC')::date FROM public.usage_events_default ORDER BY 1;
            EXECUTE 'ALTER TABLE public.usage_events_default FORCE ROW LEVEL SECURITY';
        END $f$;

        -- Drops a whole month. Refuses the previous month and anything newer: retention keeps at least one full month whatever it is told.
        CREATE FUNCTION usage_events_drop_partition(p_month date) RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $f$
        DECLARE
            v_to date := (date_trunc('month', p_month) + interval '1 month')::date;
            v_name text := 'usage_events_' || to_char(date_trunc('month', p_month), 'YYYY_MM');
        BEGIN
            IF v_to > (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::date THEN
                RAISE EXCEPTION 'Refusing to drop the usage events of % (too recent)', v_name;
            END IF;
            IF to_regclass(format('public.%I', v_name)) IS NULL THEN RETURN false; END IF;
            PERFORM set_config('lock_timeout', '10s', true);
            EXECUTE format('ALTER TABLE public.usage_events DETACH PARTITION public.%I', v_name);
            EXECUTE format('DROP TABLE public.%I', v_name);
            RETURN true;
        END $f$;

        -- Removes up to p_limit stray events older than p_day from the default partition (retention); returns how many.
        CREATE FUNCTION usage_events_discard_before(p_day date, p_limit integer) RETURNS bigint
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $f$
        DECLARE v_deleted bigint;
        BEGIN
            EXECUTE 'ALTER TABLE public.usage_events_default NO FORCE ROW LEVEL SECURITY';
            WITH d AS (
                DELETE FROM public.usage_events_default
                WHERE ctid IN (SELECT ctid FROM public.usage_events_default WHERE occurred_at < (p_day::timestamp AT TIME ZONE 'UTC') LIMIT p_limit)
                RETURNING 1)
            SELECT count(*) INTO v_deleted FROM d;
            EXECUTE 'ALTER TABLE public.usage_events_default FORCE ROW LEVEL SECURITY';
            RETURN v_deleted;
        END $f$;

        REVOKE ALL ON FUNCTION usage_events_ensure_partition(date), usage_events_partition_months(), usage_events_stray_months(),
            usage_events_drop_partition(date), usage_events_discard_before(date, integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION usage_events_ensure_partition(date), usage_events_partition_months(), usage_events_stray_months(),
            usage_events_drop_partition(date), usage_events_discard_before(date, integer) TO dam_system;
        """;

    /// <summary>
    /// Sets the old table aside, builds the partitioned one with the same columns, key and indexes (the partition key is part of the primary key; the
    /// unique client-event-id index becomes <c>usage_event_keys</c>), creates a partition for every month from the oldest event to three months ahead,
    /// copies the rows across, records their client ids and marks each day that has events for rollup (so the first rollup run builds the history), and drops
    /// the old table. All in the migration's transaction, so a failure leaves the old table untouched. RLS is switched off on the old table first only so an
    /// owner that is not a superuser can read it for the copy.
    /// </summary>
    public const string ConvertUsageEvents = """
        ALTER TABLE usage_events NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE usage_events DISABLE ROW LEVEL SECURITY;
        ALTER TABLE usage_events RENAME TO usage_events_old;
        ALTER TABLE usage_events_old DROP CONSTRAINT pk_usage_events;
        ALTER TABLE usage_events_old DROP CONSTRAINT fk_usage_events_assets_asset_id;
        DROP INDEX ix_usage_events_asset_id;
        DROP INDEX ix_usage_events_tenant_id_asset_id_occurred_at;
        DROP INDEX ix_usage_events_tenant_id_occurred_at;
        DROP INDEX ix_usage_events_tenant_id_client_event_id;

        CREATE TABLE usage_events (
            id uuid NOT NULL,
            asset_id uuid NOT NULL,
            event_type character varying(16) NOT NULL,
            channel character varying(100),
            rendition character varying(50),
            actor_id uuid,
            occurred_at timestamp with time zone NOT NULL,
            client_event_id character varying(100),
            context jsonb,
            created_at timestamp with time zone NOT NULL,
            tenant_id uuid NOT NULL,
            CONSTRAINT pk_usage_events PRIMARY KEY (id, occurred_at),
            CONSTRAINT fk_usage_events_assets_asset_id FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE CASCADE
        ) PARTITION BY RANGE (occurred_at);
        CREATE INDEX ix_usage_events_asset_id ON usage_events (asset_id);
        CREATE INDEX ix_usage_events_tenant_id_asset_id_occurred_at ON usage_events (tenant_id, asset_id, occurred_at DESC);
        CREATE INDEX ix_usage_events_tenant_id_occurred_at ON usage_events (tenant_id, occurred_at);
        CREATE TABLE usage_events_default PARTITION OF usage_events DEFAULT;

        DO $$
        DECLARE m date; last_month date;
        BEGIN
            SELECT coalesce(date_trunc('month', min(occurred_at) AT TIME ZONE 'UTC')::date, date_trunc('month', now() AT TIME ZONE 'UTC')::date) INTO m FROM usage_events_old;
            last_month := (date_trunc('month', now() AT TIME ZONE 'UTC') + interval '3 months')::date;
            WHILE m <= last_month LOOP
                PERFORM usage_events_ensure_partition(m);
                m := (m + interval '1 month')::date;
            END LOOP;
        END $$;

        INSERT INTO usage_events (id, asset_id, event_type, channel, rendition, actor_id, occurred_at, client_event_id, context, created_at, tenant_id)
            SELECT id, asset_id, event_type, channel, rendition, actor_id, occurred_at, client_event_id, context, created_at, tenant_id FROM usage_events_old;
        INSERT INTO usage_event_keys (tenant_id, client_event_id, created_at)
            SELECT tenant_id, client_event_id, min(created_at) FROM usage_events_old WHERE client_event_id IS NOT NULL GROUP BY tenant_id, client_event_id;
        INSERT INTO usage_rollup_dirty (tenant_id, day, marked_at)
            SELECT DISTINCT tenant_id, (occurred_at AT TIME ZONE 'UTC')::date, now() FROM usage_events_old;
        DROP TABLE usage_events_old;
        """;

    /// <summary>Down: back to one ordinary table with the rows of every partition, the old unique client-event-id index and the old indexes.</summary>
    public const string RevertUsageEvents = """
        CREATE TABLE usage_events_plain (LIKE usage_events INCLUDING DEFAULTS);
        INSERT INTO usage_events_plain SELECT * FROM usage_events;
        DROP TABLE usage_events CASCADE;
        ALTER TABLE usage_events_plain RENAME TO usage_events;
        ALTER TABLE usage_events ADD CONSTRAINT pk_usage_events PRIMARY KEY (id);
        ALTER TABLE usage_events ADD CONSTRAINT fk_usage_events_assets_asset_id FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE CASCADE;
        CREATE INDEX ix_usage_events_asset_id ON usage_events (asset_id);
        CREATE INDEX ix_usage_events_tenant_id_asset_id_occurred_at ON usage_events (tenant_id, asset_id, occurred_at DESC);
        CREATE INDEX ix_usage_events_tenant_id_occurred_at ON usage_events (tenant_id, occurred_at);
        CREATE UNIQUE INDEX ix_usage_events_tenant_id_client_event_id ON usage_events (tenant_id, client_event_id) WHERE client_event_id IS NOT NULL;
        ALTER TABLE usage_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE usage_events FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON usage_events
            USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
        GRANT SELECT, INSERT ON usage_events TO dam_app;
        DROP FUNCTION usage_events_ensure_partition(date), usage_events_partition_months(), usage_events_stray_months(),
            usage_events_drop_partition(date), usage_events_discard_before(date, integer);
        """;
}
