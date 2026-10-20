using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TranslationAndGlossary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ai_suggestions_once",
                table: "ai_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_suggestions_kind",
                table: "ai_suggestions");

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "asset_extracts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "summary_at",
                table: "asset_extracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary_language",
                table: "asset_extracts",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "value",
                table: "ai_suggestions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "field",
                table: "ai_suggestions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "ai_suggestions",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_hash",
                table: "ai_suggestions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_language",
                table: "ai_suggestions",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "glossary_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    term = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    translations = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_glossary_terms", x => x.id);
                    table.CheckConstraint("ck_glossary_terms_mode", "mode IN ('locked','mapped')");
                });

            migrationBuilder.CreateTable(
                name: "translation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    target_languages = table.Column<string>(type: "jsonb", nullable: false),
                    source_language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    overwrite = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_translation_jobs", x => x.id);
                    table.CheckConstraint("ck_translation_jobs_status", "status IN ('pending','running','succeeded','partial','failed')");
                    table.ForeignKey(
                        name: "fk_translation_jobs_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_suggestions_translation_review",
                table: "ai_suggestions",
                columns: new[] { "tenant_id", "status" },
                filter: "kind = 'translation' AND status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ux_ai_suggestions_once",
                table: "ai_suggestions",
                columns: new[] { "asset_id", "version_id", "kind", "value" },
                unique: true,
                filter: "kind <> 'translation'");

            migrationBuilder.CreateIndex(
                name: "ux_ai_suggestions_translation_pending",
                table: "ai_suggestions",
                columns: new[] { "asset_id", "field", "language" },
                unique: true,
                filter: "kind = 'translation' AND status = 'pending'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_suggestions_kind",
                table: "ai_suggestions",
                sql: "kind IN ('tag','language','translation')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_suggestions_translation_shape",
                table: "ai_suggestions",
                sql: "kind <> 'translation' OR (field IS NOT NULL AND language IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_suggestions_value_length",
                table: "ai_suggestions",
                sql: "kind = 'translation' OR char_length(value) <= 200");

            migrationBuilder.CreateIndex(
                name: "ux_glossary_terms_term",
                table: "glossary_terms",
                columns: new[] { "tenant_id", "term" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_translation_jobs_asset_id_id",
                table: "translation_jobs",
                columns: new[] { "asset_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_translation_jobs_tenant_id_status",
                table: "translation_jobs",
                columns: new[] { "tenant_id", "status" });

            foreach (var table in new[] { "glossary_terms", "translation_jobs" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.TranslationGrants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Translations cannot be kept: the old shape has no place for them.
            migrationBuilder.Sql("DELETE FROM ai_suggestions WHERE kind = 'translation'");

            migrationBuilder.DropTable(
                name: "glossary_terms");

            migrationBuilder.DropTable(
                name: "translation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_ai_suggestions_translation_review",
                table: "ai_suggestions");

            migrationBuilder.DropIndex(
                name: "ux_ai_suggestions_once",
                table: "ai_suggestions");

            migrationBuilder.DropIndex(
                name: "ux_ai_suggestions_translation_pending",
                table: "ai_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_suggestions_kind",
                table: "ai_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_suggestions_translation_shape",
                table: "ai_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_suggestions_value_length",
                table: "ai_suggestions");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "asset_extracts");

            migrationBuilder.DropColumn(
                name: "summary_at",
                table: "asset_extracts");

            migrationBuilder.DropColumn(
                name: "summary_language",
                table: "asset_extracts");

            migrationBuilder.DropColumn(
                name: "field",
                table: "ai_suggestions");

            migrationBuilder.DropColumn(
                name: "language",
                table: "ai_suggestions");

            migrationBuilder.DropColumn(
                name: "source_hash",
                table: "ai_suggestions");

            migrationBuilder.DropColumn(
                name: "source_language",
                table: "ai_suggestions");

            migrationBuilder.AlterColumn<string>(
                name: "value",
                table: "ai_suggestions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "ux_ai_suggestions_once",
                table: "ai_suggestions",
                columns: new[] { "asset_id", "version_id", "kind", "value" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_suggestions_kind",
                table: "ai_suggestions",
                sql: "kind IN ('tag','language')");
        }
    }
}
