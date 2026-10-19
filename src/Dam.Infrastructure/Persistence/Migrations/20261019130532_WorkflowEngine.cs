using System;
using System.Collections.Generic;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dam.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_bindings", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_bindings_workflow_definitions_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflow_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    definition_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    definition_json = table.Column<string>(type: "jsonb", nullable: false),
                    distinct_approvers = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    rounds = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_instances", x => x.id);
                    table.CheckConstraint("ck_workflow_instances_status", "status IN ('running','approved','rejected','cancelled')");
                    table.ForeignKey(
                        name: "fk_workflow_instances_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_instances_workflow_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "workflow_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "workflow_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    step_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    assignee_roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    assignee_user_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    escalated_roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    escalated_user_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sla_breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_tasks", x => x.id);
                    table.CheckConstraint("ck_workflow_tasks_status", "status IN ('pending','approved','rejected','cancelled')");
                    table.ForeignKey(
                        name: "fk_workflow_tasks_workflow_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "workflow_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            foreach (var table in new[] { "workflow_definitions", "workflow_bindings", "workflow_instances", "workflow_tasks" })
                migrationBuilder.Sql(SecuritySql.EnableTenantRls(table));
            migrationBuilder.Sql(SecuritySql.WorkflowGrants);
            migrationBuilder.Sql(SecuritySql.WorkflowSystemGrants);
            // Workflow names are unique per tenant, ignoring case (the handler checks first; this is the backstop).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_workflow_definitions_tenant_lower_name ON workflow_definitions (tenant_id, lower(name));");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_bindings_tenant_id_asset_type_region",
                table: "workflow_bindings",
                columns: new[] { "tenant_id", "asset_type", "region" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_bindings_workflow_id",
                table: "workflow_bindings",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_definitions_tenant_id_name",
                table: "workflow_definitions",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_definition_id",
                table: "workflow_instances",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_tenant_id_asset_id_started_at",
                table: "workflow_instances",
                columns: new[] { "tenant_id", "asset_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_tenant_id_status",
                table: "workflow_instances",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_instances_running_per_asset",
                table: "workflow_instances",
                column: "asset_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_assignee_roles",
                table: "workflow_tasks",
                column: "assignee_roles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_assignee_user_ids",
                table: "workflow_tasks",
                column: "assignee_user_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_instance_id_step_id_round",
                table: "workflow_tasks",
                columns: new[] { "instance_id", "step_id", "round" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_sla_sweep",
                table: "workflow_tasks",
                columns: new[] { "status", "due_at" },
                filter: "status = 'pending' AND sla_breached_at IS NULL AND due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_asset_id",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_decided_by",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "decided_by" },
                filter: "decided_by IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_status",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_bindings");

            migrationBuilder.DropTable(
                name: "workflow_tasks");

            migrationBuilder.DropTable(
                name: "workflow_instances");

            migrationBuilder.DropTable(
                name: "workflow_definitions");
        }
    }
}
