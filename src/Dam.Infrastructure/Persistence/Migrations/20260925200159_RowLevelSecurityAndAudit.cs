using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RowLevelSecurityAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.AppRole);
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("outbox"));
            migrationBuilder.Sql(SecuritySql.EnableTenantRls("audit_log"));
            migrationBuilder.Sql(SecuritySql.AuditChain);
            migrationBuilder.Sql(SecuritySql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_verify(uuid), audit_log_chain(), audit_log_immutable() CASCADE;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_row_hash(text, audit_log);");
        }
    }
}
