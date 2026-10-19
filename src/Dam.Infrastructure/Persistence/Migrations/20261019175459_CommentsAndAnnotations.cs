using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommentsAndAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    anchor = table.Column<string>(type: "jsonb", nullable: true),
                    anchor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolved = table.Column<bool>(type: "boolean", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.CheckConstraint("ck_comments_anchor_type", "anchor_type IS NULL OR anchor_type IN ('region','point','timecode')");
                    table.ForeignKey(
                        name: "fk_comments_asset_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "asset_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_comments_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comments_comments_parent_id",
                        column: x => x.parent_id,
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment_mentions",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_mentions", x => new { x.comment_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_comment_mentions_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            foreach (var table in new[] { "comments", "comment_mentions" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.CommentGrants);

            migrationBuilder.CreateIndex(
                name: "ix_comment_mentions_tenant_id_user_id",
                table: "comment_mentions",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_asset_id",
                table: "comments",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_parent_id",
                table: "comments",
                column: "parent_id",
                filter: "parent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_comments_tenant_id_asset_id_anchor_type",
                table: "comments",
                columns: new[] { "tenant_id", "asset_id", "anchor_type" },
                filter: "anchor_type IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_comments_tenant_id_asset_id_created_at",
                table: "comments",
                columns: new[] { "tenant_id", "asset_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_tenant_id_asset_id_resolved",
                table: "comments",
                columns: new[] { "tenant_id", "asset_id", "resolved" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_version_id",
                table: "comments",
                column: "version_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comment_mentions");

            migrationBuilder.DropTable(
                name: "comments");
        }
    }
}
