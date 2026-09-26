using System;
using System.Collections.Generic;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetsAndUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    metadata_schema_version = table.Column<int>(type: "integer", nullable: false),
                    taxonomy_terms = table.Column<List<string>>(type: "text[]", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rights_id = table.Column<Guid>(type: "uuid", nullable: true),
                    publish_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retention_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checksum_sha256 = table.Column<string>(type: "char(64)", nullable: true),
                    phash = table.Column<string>(type: "text", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.CheckConstraint("ck_assets_status", "status IN ('draft','in_review','approved','published','expired','archived','deleted','blocked')");
                    table.CheckConstraint("ck_assets_type", "type IN ('image','video','audio','document','manual','3d','html5','other')");
                    table.ForeignKey(
                        name: "fk_assets_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    declared_size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    s3upload_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    part_size = table.Column<int>(type: "integer", nullable: false),
                    part_count = table.Column<int>(type: "integer", nullable: false),
                    allow_duplicate = table.Column<bool>(type: "boolean", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    duplicate_of_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploads", x => x.id);
                    table.ForeignKey(
                        name: "fk_uploads_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_folder_id",
                table: "assets",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_checksum_sha256",
                table: "assets",
                columns: new[] { "tenant_id", "checksum_sha256" },
                filter: "checksum_sha256 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_folder_id",
                table: "assets",
                columns: new[] { "tenant_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_status",
                table: "assets",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_uploads_asset_id",
                table: "uploads",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_uploads_storage_key",
                table: "uploads",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_uploads_tenant_id_asset_id",
                table: "uploads",
                columns: new[] { "tenant_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_uploads_tenant_id_batch_id",
                table: "uploads",
                columns: new[] { "tenant_id", "batch_id" },
                filter: "batch_id IS NOT NULL");
                    migrationBuilder.Sql(SecuritySql.EnableTenantRls("assets"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("uploads"));
            migrationBuilder.Sql(SecuritySql.AssetGrants);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "uploads");

            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
