using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsWebhooksAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "idp_roles",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url_cipher = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    url_hint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    events = table.Column<List<string>>(type: "text[]", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                    table.CheckConstraint("ck_notification_channels_kind", "kind IN ('slack','teams')");
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recipient = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    in_app = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    link_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "siem_cursors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_chain_no = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_siem_cursors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    secret_cipher = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    secret_last4 = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    events = table.Column<List<string>>(type: "text[]", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhooks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_duration_ms = table.Column<int>(type: "integer", nullable: true),
                    response_excerpt = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replay_of = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.CheckConstraint("ck_webhook_deliveries_status", "status IN ('pending','succeeded','failed')");
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhooks_webhook_id",
                        column: x => x.webhook_id,
                        principalTable: "webhooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            foreach (var table in new[] { "webhooks", "webhook_deliveries", "notifications", "notification_preferences", "notification_channels", "notification_deliveries", "siem_cursors" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.NotificationGrants);
            migrationBuilder.Sql(SecuritySql.WebhookSystemGrants);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_ts",
                table: "audit_log",
                columns: new[] { "tenant_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_tenant_id_active",
                table: "notification_channels",
                columns: new[] { "tenant_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_event_id_channel_recipient",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "event_id", "channel", "recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_tenant_id_user_id_category",
                table: "notification_preferences",
                columns: new[] { "tenant_id", "user_id", "category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_user_id_id",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_unread",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id" },
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_siem_cursors_tenant_id",
                table: "siem_cursors",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_due",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_replay_of",
                table: "webhook_deliveries",
                column: "replay_of",
                filter: "replay_of IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_status_id",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_webhook_id_id",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "webhook_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_webhook_deliveries_event",
                table: "webhook_deliveries",
                columns: new[] { "webhook_id", "event_id" },
                unique: true,
                filter: "replay_of IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhooks_tenant_id_active",
                table: "webhooks",
                columns: new[] { "tenant_id", "active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "siem_cursors");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhooks");

            migrationBuilder.DropIndex(
                name: "ix_audit_log_tenant_id_ts",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "idp_roles",
                table: "users");
        }
    }
}
