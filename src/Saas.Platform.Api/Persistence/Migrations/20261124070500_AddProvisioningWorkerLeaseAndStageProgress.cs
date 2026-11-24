using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saas.Platform.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningWorkerLeaseAndStageProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "clinical_migration_version",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "clinical_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.AddColumn<string>(
                name: "identity_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.AddColumn<string>(
                name: "infrastructure_resource_ref",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "infrastructure_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.AddColumn<string>(
                name: "initial_admin_account_id",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_owner",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until_utc",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "lease_version",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "manual_intervention_required",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at_utc",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "service_databases_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_provisioning_operations_worker_acquisition",
                schema: "platform",
                table: "tenant_provisioning_operations",
                columns: new[] { "status", "manual_intervention_required", "next_attempt_at_utc", "lease_until_utc" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_admin_acct_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "initial_admin_account_id IS NULL OR char_length(btrim(initial_admin_account_id)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_clinical_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "clinical_stage_state IN ('PENDING', 'VERIFICATION_REQUIRED', 'CONFIRMED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_clinical_ver_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "clinical_migration_version IS NULL OR char_length(btrim(clinical_migration_version)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_identity_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "identity_stage_state IN ('PENDING', 'VERIFICATION_REQUIRED', 'CONFIRMED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_in_progress_has_lease",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "status <> 'IN_PROGRESS' OR (lease_owner IS NOT NULL AND lease_until_utc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_infra_ref_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "infrastructure_resource_ref IS NULL OR char_length(btrim(infrastructure_resource_ref)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_infrastructure_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "infrastructure_stage_state IN ('PENDING', 'VERIFICATION_REQUIRED', 'CONFIRMED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_lease_consistency",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "(lease_owner IS NULL AND lease_until_utc IS NULL) OR (lease_owner IS NOT NULL AND char_length(btrim(lease_owner)) > 0 AND lease_until_utc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_lease_version_non_negative",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "lease_version >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_next_attempt_status",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "next_attempt_at_utc IS NULL OR status = 'FAILED_RETRYABLE'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_non_in_progress_no_lease",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "status = 'IN_PROGRESS' OR (lease_owner IS NULL AND lease_until_utc IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_provisioning_operations_service_databases_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations",
                sql: "service_databases_stage_state IN ('PENDING', 'VERIFICATION_REQUIRED', 'CONFIRMED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_provisioning_operations_worker_acquisition",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_admin_acct_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_clinical_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_clinical_ver_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_identity_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_in_progress_has_lease",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_infra_ref_not_blank",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_infrastructure_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_lease_consistency",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_lease_version_non_negative",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_next_attempt_status",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_non_in_progress_no_lease",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_provisioning_operations_service_databases_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "clinical_migration_version",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "clinical_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "identity_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "infrastructure_resource_ref",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "infrastructure_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "initial_admin_account_id",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "lease_owner",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "lease_until_utc",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "lease_version",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "manual_intervention_required",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "next_attempt_at_utc",
                schema: "platform",
                table: "tenant_provisioning_operations");

            migrationBuilder.DropColumn(
                name: "service_databases_stage_state",
                schema: "platform",
                table: "tenant_provisioning_operations");
        }
    }
}
