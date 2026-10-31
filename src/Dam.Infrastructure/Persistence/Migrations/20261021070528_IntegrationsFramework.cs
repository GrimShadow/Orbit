using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationsFramework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    secrets = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_ok_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    token_cipher = table.Column<string>(type: "text", nullable: true),
                    login_lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    test_claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connections", x => x.id);
                    table.CheckConstraint("ck_integration_connections_status", "status IN ('new','ok','error')");
                });

            migrationBuilder.CreateTable(
                name: "integration_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    target = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_calls", x => x.id);
                    table.CheckConstraint("ck_integration_calls_kind", "kind IN ('auth','test','action')");
                    table.ForeignKey(
                        name: "fk_integration_calls_integration_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_calls_connection_id_id",
                table: "integration_calls",
                columns: new[] { "connection_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_calls_tenant_id",
                table: "integration_calls",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connections_tenant_id",
                table: "integration_connections",
                column: "tenant_id");

            foreach (var table in new[] { "integration_connections", "integration_calls" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.IntegrationGrants);
            migrationBuilder.Sql(SecuritySql.IntegrationSystemGrants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_calls");

            migrationBuilder.DropTable(
                name: "integration_connections");
        }
    }
}
