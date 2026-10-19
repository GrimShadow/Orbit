using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RightsAndWatermarking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "metadata_attributes",
                table: "assets",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<List<string>>(
                name: "rights_channels",
                table: "assets",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "rights_regions",
                table: "assets",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            // Until now an asset's attributes were derived from its metadata alone, so that is what they are before any rights narrow them.
            migrationBuilder.Sql("UPDATE assets SET metadata_attributes = attributes");

            migrationBuilder.CreateTable(
                name: "rights",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    licence_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    licensor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    channels = table.Column<List<string>>(type: "text[]", nullable: false),
                    regions = table.Column<List<string>>(type: "text[]", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    watermark_required = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rights", x => x.id);
                    table.CheckConstraint("ck_rights_licence_type", "licence_type IN ('owned','licensed','royalty_free','rights_managed','editorial','creative_commons','other')");
                });

            migrationBuilder.CreateTable(
                name: "rights_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rights_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold_days = table.Column<int>(type: "integer", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rights_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_rights_alerts_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_rights_alerts_rights_rights_id",
                        column: x => x.rights_id,
                        principalTable: "rights",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rights_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rights_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rights_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_rights_documents_rights_rights_id",
                        column: x => x.rights_id,
                        principalTable: "rights",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_rights_id",
                table: "assets",
                column: "rights_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_rights_id",
                table: "assets",
                columns: new[] { "tenant_id", "rights_id" },
                filter: "rights_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rights_tenant_id",
                table: "rights",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rights_valid_to",
                table: "rights",
                column: "valid_to",
                filter: "valid_to IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rights_alerts_rights_id",
                table: "rights_alerts",
                column: "rights_id");

            migrationBuilder.CreateIndex(
                name: "ux_rights_alerts_once",
                table: "rights_alerts",
                columns: new[] { "asset_id", "rights_id", "threshold_days", "valid_to" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rights_documents_rights_id",
                table: "rights_documents",
                column: "rights_id");

            migrationBuilder.CreateIndex(
                name: "ix_rights_documents_storage_key",
                table: "rights_documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rights_documents_tenant_id_rights_id",
                table: "rights_documents",
                columns: new[] { "tenant_id", "rights_id" });

            foreach (var table in new[] { "rights", "rights_documents", "rights_alerts" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.RightsGrants);
            migrationBuilder.Sql(SecuritySql.RightsSystemGrants);

            migrationBuilder.AddForeignKey(
                name: "fk_assets_rights_rights_id",
                table: "assets",
                column: "rights_id",
                principalTable: "rights",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_rights_rights_id",
                table: "assets");

            migrationBuilder.DropTable(
                name: "rights_alerts");

            migrationBuilder.DropTable(
                name: "rights_documents");

            migrationBuilder.DropTable(
                name: "rights");

            migrationBuilder.DropIndex(
                name: "ix_assets_rights_id",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_tenant_id_rights_id",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "metadata_attributes",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "rights_channels",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "rights_regions",
                table: "assets");
        }
    }
}
