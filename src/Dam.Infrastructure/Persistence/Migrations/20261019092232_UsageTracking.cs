using System;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_usage",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    use_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_usage", x => x.asset_id);
                    table.ForeignKey(
                        name: "fk_asset_usage_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usage_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    rendition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    client_event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    context = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_usage_events_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(SecuritySql.EnableTenantRls("usage_events"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("asset_usage"));
            migrationBuilder.Sql(SecuritySql.UsageGrants);

            migrationBuilder.CreateIndex(
                name: "ix_asset_usage_tenant_id_last_used_at",
                table: "asset_usage",
                columns: new[] { "tenant_id", "last_used_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_asset_id",
                table: "usage_events",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_tenant_id_asset_id_occurred_at",
                table: "usage_events",
                columns: new[] { "tenant_id", "asset_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_tenant_id_client_event_id",
                table: "usage_events",
                columns: new[] { "tenant_id", "client_event_id" },
                unique: true,
                filter: "client_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_tenant_id_occurred_at",
                table: "usage_events",
                columns: new[] { "tenant_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_usage");

            migrationBuilder.DropTable(
                name: "usage_events");
        }
    }
}
