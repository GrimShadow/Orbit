using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_runs", x => x.id);
                    table.CheckConstraint("ck_ai_runs_status", "status IN ('pending','running','succeeded','failed','skipped')");
                    table.ForeignKey(
                        name: "fk_ai_runs_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    features = table.Column<string>(type: "jsonb", nullable: false),
                    ocr_language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_settings", x => x.tenant_id);
                    table.CheckConstraint("ck_ai_settings_provider", "provider IS NULL OR provider IN ('self-hosted','openai-compatible','none')");
                });

            migrationBuilder.CreateTable(
                name: "ai_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    confidence = table.Column<float>(type: "real", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_suggestions", x => x.id);
                    table.CheckConstraint("ck_ai_suggestions_kind", "kind IN ('tag','language')");
                    table.CheckConstraint("ck_ai_suggestions_status", "status IN ('pending','accepted','rejected')");
                    table.ForeignKey(
                        name: "fk_ai_suggestions_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_embeddings",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dim = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    vector = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_embeddings", x => new { x.asset_id, x.version_id, x.kind });
                    table.CheckConstraint("ck_asset_embeddings_dim", "dim > 0 AND octet_length(vector) = dim * 4");
                    table.CheckConstraint("ck_asset_embeddings_kind", "kind IN ('image','text')");
                    table.ForeignKey(
                        name: "fk_asset_embeddings_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_extracts",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ocr_text = table.Column<string>(type: "text", nullable: true),
                    ocr_language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    transcript_language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    transcript_segments = table.Column<string>(type: "jsonb", nullable: false),
                    transcript_text = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    model = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_extracts", x => new { x.asset_id, x.version_id });
                    table.ForeignKey(
                        name: "fk_asset_extracts_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_runs_tenant_id_status",
                table: "ai_runs",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_runs_version",
                table: "ai_runs",
                columns: new[] { "asset_id", "version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_suggestions_tenant_id_asset_id_status",
                table: "ai_suggestions",
                columns: new[] { "tenant_id", "asset_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_suggestions_once",
                table: "ai_suggestions",
                columns: new[] { "asset_id", "version_id", "kind", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_embeddings_tenant_id",
                table: "asset_embeddings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_extracts_tenant_id",
                table: "asset_extracts",
                column: "tenant_id");

            foreach (var table in new[] { "ai_settings", "ai_suggestions", "asset_extracts", "asset_embeddings", "ai_runs" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.AiGrants);
            migrationBuilder.Sql(SecuritySql.AiSystemGrants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_runs");

            migrationBuilder.DropTable(
                name: "ai_settings");

            migrationBuilder.DropTable(
                name: "ai_suggestions");

            migrationBuilder.DropTable(
                name: "asset_embeddings");

            migrationBuilder.DropTable(
                name: "asset_extracts");
        }
    }
}
