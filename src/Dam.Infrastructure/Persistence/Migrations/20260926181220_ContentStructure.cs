using System;
using System.Collections.Generic;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    inherited_metadata = table.Column<string>(type: "jsonb", nullable: false),
                    default_workflow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folders", x => x.id);
                    table.CheckConstraint("ck_folders_path", "path ~ '^[a-z0-9_]{1,64}(\\.[a-z0-9_]{1,64}){0,11}$'");
                });

            migrationBuilder.CreateTable(
                name: "metadata_schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    change_note = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_schemas", x => x.id);
                    table.CheckConstraint("ck_metadata_schemas_type", "asset_type IN ('image','video','audio','document','manual','3d','html5','other')");
                });

            migrationBuilder.CreateTable(
                name: "terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vocabulary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    path = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terms", x => x.id);
                    table.CheckConstraint("ck_terms_path", "path ~ '^[a-z0-9_]{1,64}(\\.[a-z0-9_]{1,64}){0,11}$'");
                });

            migrationBuilder.CreateTable(
                name: "vocabularies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    hierarchical = table.Column<bool>(type: "boolean", nullable: false),
                    levels = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vocabularies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_folders_tenant_id_parent_id_label",
                table: "folders",
                columns: new[] { "tenant_id", "parent_id", "label" },
                unique: true,
                filter: "parent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_folders_tenant_id_path",
                table: "folders",
                columns: new[] { "tenant_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_folders_root_label",
                table: "folders",
                columns: new[] { "tenant_id", "label" },
                unique: true,
                filter: "parent_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_metadata_schemas_tenant_id_asset_type_version",
                table: "metadata_schemas",
                columns: new[] { "tenant_id", "asset_type", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_metadata_schemas_current",
                table: "metadata_schemas",
                columns: new[] { "tenant_id", "asset_type" },
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "ix_terms_tenant_id_vocabulary_id_code",
                table: "terms",
                columns: new[] { "tenant_id", "vocabulary_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_terms_tenant_id_vocabulary_id_parent_id",
                table: "terms",
                columns: new[] { "tenant_id", "vocabulary_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vocabularies_tenant_id_key",
                table: "vocabularies",
                columns: new[] { "tenant_id", "key" },
                unique: true);
                    migrationBuilder.Sql(SecuritySql.EnableTenantRls("folders"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("vocabularies"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("terms"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("metadata_schemas"));
            migrationBuilder.Sql(SecuritySql.ContentGrants);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "metadata_schemas");

            migrationBuilder.DropTable(
                name: "terms");

            migrationBuilder.DropTable(
                name: "vocabularies");
        }
    }
}
