using System;
using System.Collections.Generic;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsageRollupsAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "usage_rollup_mark_seq");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_published_at",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "report_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hour_utc = table.Column<int>(type: "integer", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: true),
                    day_of_month = table.Column<int>(type: "integer", nullable: true),
                    recipient_user_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_to = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_schedules", x => x.id);
                    table.CheckConstraint("ck_report_schedules_frequency", "frequency IN ('daily','weekly','monthly')");
                    table.CheckConstraint("ck_report_schedules_hour", "hour_utc BETWEEN 0 AND 23");
                });

            migrationBuilder.CreateTable(
                name: "usage_daily",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    uses = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_daily", x => new { x.tenant_id, x.day, x.asset_id, x.event_type, x.channel });
                    table.ForeignKey(
                        name: "fk_usage_daily_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usage_event_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_event_keys", x => new { x.tenant_id, x.client_event_id });
                });

            migrationBuilder.CreateTable(
                name: "usage_rollup_dirty",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    mark_seq = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('usage_rollup_mark_seq')"),
                    marked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_rollup_dirty", x => new { x.tenant_id, x.day });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_decided_at",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "decided_at" },
                filter: "decided_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_sla_breached_at",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "sla_breached_at" },
                filter: "sla_breached_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_uploads_tenant_id_created_at",
                table: "uploads",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_first_published_at",
                table: "assets",
                columns: new[] { "tenant_id", "first_published_at" },
                filter: "first_published_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_due",
                table: "report_schedules",
                column: "next_run_at",
                filter: "enabled AND next_run_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_tenant_id",
                table: "report_schedules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_daily_asset_id",
                table: "usage_daily",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_daily_tenant_id_asset_id_day",
                table: "usage_daily",
                columns: new[] { "tenant_id", "asset_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_event_keys_created_at",
                table: "usage_event_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_rollup_dirty_day",
                table: "usage_rollup_dirty",
                column: "day");

            // ---- publishing effort (ADR-0037): assets.first_published_at, back-filled from the audit trail's first asset.status.changed to published.
            // (Run by the database owner, which in the shipped stack is a superuser: the audit trail and assets have forced RLS, so an owner that is neither
            // would read no rows here and leave the column null for existing assets; new publishes are unaffected.)
            migrationBuilder.Sql(UsageMigrationSql.BackfillFirstPublished);

            // ---- partition maintenance functions (security definer: the app roles cannot run DDL), then the partitioned table and the data
            migrationBuilder.Sql(UsageMigrationSql.Functions);
            migrationBuilder.Sql(UsageMigrationSql.ConvertUsageEvents);

            // ---- security: RLS on every new table, grants, and the cross-tenant reads the jobs need
            foreach (var table in new[] { "usage_events", "usage_event_keys", "usage_daily", "usage_rollup_dirty", "report_schedules" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("usage_events_default"));
            migrationBuilder.Sql(SecuritySql.UsageGrants);
            migrationBuilder.Sql(SecuritySql.AnalyticsGrants);
            migrationBuilder.Sql(SecuritySql.AnalyticsSystemGrants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UsageMigrationSql.RevertUsageEvents);

            migrationBuilder.DropTable(
                name: "report_schedules");

            migrationBuilder.DropTable(
                name: "usage_daily");

            migrationBuilder.DropTable(
                name: "usage_event_keys");

            migrationBuilder.DropTable(
                name: "usage_rollup_dirty");

            migrationBuilder.DropIndex(
                name: "ix_workflow_tasks_tenant_id_decided_at",
                table: "workflow_tasks");

            migrationBuilder.DropIndex(
                name: "ix_workflow_tasks_tenant_id_sla_breached_at",
                table: "workflow_tasks");

            migrationBuilder.DropIndex(
                name: "ix_uploads_tenant_id_created_at",
                table: "uploads");

            migrationBuilder.DropIndex(
                name: "ix_assets_tenant_id_first_published_at",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "first_published_at",
                table: "assets");

            migrationBuilder.DropSequence(
                name: "usage_rollup_mark_seq");
        }
    }
}
