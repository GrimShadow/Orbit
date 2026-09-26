using System;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetVersionsAndIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detected_mime",
                table: "uploads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ingest_stage",
                table: "uploads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "technical_metadata",
                table: "uploads",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "phash",
                table: "assets",
                type: "char(16)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "asset_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mime = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    technical_metadata = table.Column<string>(type: "jsonb", nullable: false),
                    change_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_versions_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_versions_asset_id",
                table: "asset_versions",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_versions_storage_key",
                table: "asset_versions",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_versions_tenant_id_asset_id_version_no",
                table: "asset_versions",
                columns: new[] { "tenant_id", "asset_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_versions_upload_id",
                table: "asset_versions",
                column: "upload_id",
                unique: true);
                    migrationBuilder.Sql(SecuritySql.EnableTenantRls("asset_versions"));
            migrationBuilder.Sql(SecuritySql.VersionGrants);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_versions");

            migrationBuilder.DropColumn(
                name: "detected_mime",
                table: "uploads");

            migrationBuilder.DropColumn(
                name: "ingest_stage",
                table: "uploads");

            migrationBuilder.DropColumn(
                name: "technical_metadata",
                table: "uploads");

            migrationBuilder.AlterColumn<string>(
                name: "phash",
                table: "assets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "char(16)",
                oldNullable: true);
        }
    }
}
