using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoPdfRenditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_renditions_tenant_id_version_id_profile",
                table: "renditions");

            migrationBuilder.AddColumn<double>(
                name: "duration",
                table: "renditions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_no",
                table: "renditions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_renditions_tenant_id_version_id_profile_page_no",
                table: "renditions",
                columns: new[] { "tenant_id", "version_id", "profile", "page_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_renditions_tenant_id_version_id_profile_page_no",
                table: "renditions");

            migrationBuilder.DropColumn(
                name: "duration",
                table: "renditions");

            migrationBuilder.DropColumn(
                name: "page_no",
                table: "renditions");

            migrationBuilder.CreateIndex(
                name: "ix_renditions_tenant_id_version_id_profile",
                table: "renditions",
                columns: new[] { "tenant_id", "version_id", "profile" },
                unique: true);
        }
    }
}
