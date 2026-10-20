using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeAndVinRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    variant = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model_year_from = table.Column<int>(type: "integer", nullable: true),
                    model_year_to = table.Column<int>(type: "integer", nullable: true),
                    language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    theme = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    chapters = table.Column<string>(type: "jsonb", nullable: false),
                    chapters_source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    chapters_indexed_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chapters_indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_documents", x => x.id);
                    table.CheckConstraint("ck_knowledge_documents_chapters_source", "chapters_source IN ('none','pdf','html','manual')");
                    table.CheckConstraint("ck_knowledge_documents_doc_type", "doc_type IN ('owner_manual','faq','service','training','product_info')");
                    table.CheckConstraint("ck_knowledge_documents_years", "(model_year_from IS NULL AND model_year_to IS NULL) OR (model_year_from IS NOT NULL AND model_year_to IS NOT NULL AND model_year_from <= model_year_to)");
                    table.ForeignKey(
                        name: "fk_knowledge_documents_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vin_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    pattern = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    variant = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model_year = table.Column<int>(type: "integer", nullable: true),
                    year_from_vin = table.Column<bool>(type: "boolean", nullable: false),
                    theme = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vin_rules", x => x.id);
                    table.CheckConstraint("ck_vin_rules_year", "model_year IS NULL OR (model_year BETWEEN 1980 AND 2100)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_documents_tenant_id_doc_type_model",
                table: "knowledge_documents",
                columns: new[] { "tenant_id", "doc_type", "model" });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_documents_asset",
                table: "knowledge_documents",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vin_rules_tenant_id",
                table: "vin_rules",
                column: "tenant_id");

            foreach (var table in new[] { "knowledge_documents", "vin_rules" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.KnowledgeGrants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_documents");

            migrationBuilder.DropTable(
                name: "vin_rules");
        }
    }
}
