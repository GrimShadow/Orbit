using System;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionAndLegalHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "legal_hold",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "retention_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    archive_after_days = table.Column<int>(type: "integer", nullable: true),
                    delete_after_days = table.Column<int>(type: "integer", nullable: true),
                    legal_hold_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                });

            migrationBuilder.Sql(SecuritySql.EnableTenantRls("retention_policies"));
            migrationBuilder.Sql(SecuritySql.RetentionGrants);
            migrationBuilder.Sql(SecuritySql.SchedulerSystemGrants);

            migrationBuilder.CreateIndex(
                name: "ix_assets_retention_policy_id",
                table: "assets",
                column: "retention_policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_status_expire_at",
                table: "assets",
                columns: new[] { "status", "expire_at" },
                filter: "expire_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_status_publish_at",
                table: "assets",
                columns: new[] { "status", "publish_at" },
                filter: "publish_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_status_retention_policy_id",
                table: "assets",
                columns: new[] { "status", "retention_policy_id" },
                filter: "retention_policy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_tenant_id_name",
                table: "retention_policies",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_assets_retention_policies_retention_policy_id",
                table: "assets",
                column: "retention_policy_id",
                principalTable: "retention_policies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_retention_policies_retention_policy_id",
                table: "assets");

            migrationBuilder.DropTable(
                name: "retention_policies");

            migrationBuilder.DropIndex(
                name: "ix_assets_retention_policy_id",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_status_expire_at",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_status_publish_at",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_status_retention_policy_id",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "legal_hold",
                table: "assets");
        }
    }
}
