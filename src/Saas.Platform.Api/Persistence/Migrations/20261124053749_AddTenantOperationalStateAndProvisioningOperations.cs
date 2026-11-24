using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saas.Platform.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOperationalStateAndProvisioningOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "operational_state",
                schema: "platform",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PENDIENTE_ADMIN");

            migrationBuilder.CreateTable(
                name: "tenant_provisioning_operations",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provisioning_request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    initial_admin_full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    initial_admin_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_provisioning_operations", x => x.id);
                    table.CheckConstraint("ck_tenant_provisioning_operations_admin_email_not_empty", "char_length(btrim(initial_admin_email)) >= 3");
                    table.CheckConstraint("ck_tenant_provisioning_operations_admin_name_length", "char_length(btrim(initial_admin_full_name)) >= 2");
                    table.CheckConstraint("ck_tenant_provisioning_operations_attempt_count_non_negative", "attempt_count >= 0");
                    table.CheckConstraint("ck_tenant_provisioning_operations_attempt_timestamp_consistency", "(attempt_count = 0 AND last_attempt_at_utc IS NULL) OR (attempt_count > 0 AND last_attempt_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_tenant_provisioning_operations_completed_at_consistency", "(status = 'COMPLETED' AND completed_at_utc IS NOT NULL) OR (status <> 'COMPLETED' AND completed_at_utc IS NULL)");
                    table.CheckConstraint("ck_tenant_provisioning_operations_last_error_code_not_blank", "last_error_code IS NULL OR char_length(btrim(last_error_code)) > 0");
                    table.CheckConstraint("ck_tenant_provisioning_operations_last_error_summary_not_blank", "last_error_summary IS NULL OR char_length(btrim(last_error_summary)) > 0");
                    table.CheckConstraint("ck_tenant_provisioning_operations_request_id_not_empty", "char_length(btrim(provisioning_request_id)) >= 8");
                    table.CheckConstraint("ck_tenant_provisioning_operations_status", "status IN ('PENDING', 'IN_PROGRESS', 'FAILED_RETRYABLE', 'FAILED_TERMINAL', 'COMPLETED')");
                    table.ForeignKey(
                        name: "fk_tenant_provisioning_operations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_operational_state",
                schema: "platform",
                table: "tenants",
                sql: "operational_state IN ('PENDIENTE_ADMIN', 'ACTIVA', 'SUSPENDIDA')");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_provisioning_operations_status_created_at_utc",
                schema: "platform",
                table: "tenant_provisioning_operations",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_provisioning_operations_provisioning_request_id",
                schema: "platform",
                table: "tenant_provisioning_operations",
                column: "provisioning_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_provisioning_operations_tenant_id",
                schema: "platform",
                table: "tenant_provisioning_operations",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_provisioning_operations",
                schema: "platform");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_operational_state",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "operational_state",
                schema: "platform",
                table: "tenants");
        }
    }
}
