using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Persistence.Configurations;

internal sealed class TenantProvisioningOperationConfiguration : IEntityTypeConfiguration<TenantProvisioningOperation>
{
    public void Configure(EntityTypeBuilder<TenantProvisioningOperation> builder)
    {
        builder.ToTable("tenant_provisioning_operations", PlatformDbContext.SchemaName, tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_request_id_not_empty",
                "char_length(btrim(provisioning_request_id)) >= 8");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_admin_name_length",
                $"char_length(btrim(initial_admin_full_name)) >= {TenantProvisioningOperation.InitialAdminFullNameMinLength}");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_admin_email_not_empty",
                "char_length(btrim(initial_admin_email)) >= 3");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_status",
                $"status IN ('{ProvisioningOperationStatusExtensions.PendingCode}', '{ProvisioningOperationStatusExtensions.InProgressCode}', '{ProvisioningOperationStatusExtensions.FailedRetryableCode}', '{ProvisioningOperationStatusExtensions.FailedTerminalCode}', '{ProvisioningOperationStatusExtensions.CompletedCode}')");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_attempt_count_non_negative",
                "attempt_count >= 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_last_error_code_not_blank",
                "last_error_code IS NULL OR char_length(btrim(last_error_code)) > 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_last_error_summary_not_blank",
                "last_error_summary IS NULL OR char_length(btrim(last_error_summary)) > 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_attempt_timestamp_consistency",
                "(attempt_count = 0 AND last_attempt_at_utc IS NULL) OR (attempt_count > 0 AND last_attempt_at_utc IS NOT NULL)");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_completed_at_consistency",
                $"(status = '{ProvisioningOperationStatusExtensions.CompletedCode}' AND completed_at_utc IS NOT NULL) OR (status <> '{ProvisioningOperationStatusExtensions.CompletedCode}' AND completed_at_utc IS NULL)");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_lease_version_non_negative",
                "lease_version >= 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_lease_consistency",
                "(lease_owner IS NULL AND lease_until_utc IS NULL) OR (lease_owner IS NOT NULL AND char_length(btrim(lease_owner)) > 0 AND lease_until_utc IS NOT NULL)");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_in_progress_has_lease",
                $"status <> '{ProvisioningOperationStatusExtensions.InProgressCode}' OR (lease_owner IS NOT NULL AND lease_until_utc IS NOT NULL)");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_non_in_progress_no_lease",
                $"status = '{ProvisioningOperationStatusExtensions.InProgressCode}' OR (lease_owner IS NULL AND lease_until_utc IS NULL)");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_next_attempt_status",
                $"next_attempt_at_utc IS NULL OR status = '{ProvisioningOperationStatusExtensions.FailedRetryableCode}'");

            var stageValuesSql =
                $"('{ProvisioningStageStateExtensions.PendingCode}', '{ProvisioningStageStateExtensions.VerificationRequiredCode}', '{ProvisioningStageStateExtensions.ConfirmedCode}')";

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_infrastructure_stage_state",
                $"infrastructure_stage_state IN {stageValuesSql}");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_service_databases_stage_state",
                $"service_databases_stage_state IN {stageValuesSql}");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_clinical_stage_state",
                $"clinical_stage_state IN {stageValuesSql}");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_identity_stage_state",
                $"identity_stage_state IN {stageValuesSql}");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_infra_ref_not_blank",
                "infrastructure_resource_ref IS NULL OR char_length(btrim(infrastructure_resource_ref)) > 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_clinical_ver_not_blank",
                "clinical_migration_version IS NULL OR char_length(btrim(clinical_migration_version)) > 0");

            tableBuilder.HasCheckConstraint(
                "ck_tenant_provisioning_operations_admin_acct_not_blank",
                "initial_admin_account_id IS NULL OR char_length(btrim(initial_admin_account_id)) > 0");
        });

        builder.HasKey(operation => operation.Id)
            .HasName("pk_tenant_provisioning_operations");

        builder.Property(operation => operation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(operation => operation.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(operation => operation.ProvisioningRequestId)
            .HasColumnName("provisioning_request_id")
            .HasMaxLength(TenantProvisioningOperation.ProvisioningRequestIdMaxLength)
            .IsRequired();

        builder.Property(operation => operation.InitialAdminFullName)
            .HasColumnName("initial_admin_full_name")
            .HasMaxLength(TenantProvisioningOperation.InitialAdminFullNameMaxLength)
            .IsRequired();

        builder.Property(operation => operation.InitialAdminEmail)
            .HasColumnName("initial_admin_email")
            .HasMaxLength(TenantProvisioningOperation.InitialAdminEmailMaxLength)
            .IsRequired();

        builder.Property(operation => operation.Status)
            .HasColumnName("status")
            .HasMaxLength(TenantProvisioningOperation.StatusMaxLength)
            .HasConversion(
                status => status.ToCode(),
                code => ProvisioningOperationStatusExtensions.ParseCode(code))
            .IsRequired();

        builder.Property(operation => operation.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(operation => operation.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasMaxLength(TenantProvisioningOperation.LastErrorCodeMaxLength);

        builder.Property(operation => operation.LastErrorSummary)
            .HasColumnName("last_error_summary")
            .HasMaxLength(TenantProvisioningOperation.LastErrorSummaryMaxLength);

        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();

        builder.Property(operation => operation.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .IsRequired();

        builder.Property(operation => operation.LastAttemptAtUtc)
            .HasColumnName("last_attempt_at_utc");

        builder.Property(operation => operation.CompletedAtUtc)
            .HasColumnName("completed_at_utc");

        builder.Property(operation => operation.ConcurrencyStamp)
            .HasColumnName("concurrency_stamp")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(operation => operation.LeaseOwner)
            .HasColumnName("lease_owner")
            .HasMaxLength(TenantProvisioningOperation.LeaseOwnerMaxLength);

        builder.Property(operation => operation.LeaseUntilUtc)
            .HasColumnName("lease_until_utc");

        builder.Property(operation => operation.LeaseVersion)
            .HasColumnName("lease_version")
            .HasDefaultValue(0L)
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(operation => operation.NextAttemptAtUtc)
            .HasColumnName("next_attempt_at_utc");

        builder.Property(operation => operation.ManualInterventionRequired)
            .HasColumnName("manual_intervention_required")
            .HasDefaultValue(false)
            .ValueGeneratedNever()
            .IsRequired();

        ConfigureStageProperty(builder.Property(operation => operation.InfrastructureStageState), "infrastructure_stage_state");
        ConfigureStageProperty(builder.Property(operation => operation.ServiceDatabasesStageState), "service_databases_stage_state");
        ConfigureStageProperty(builder.Property(operation => operation.ClinicalStageState), "clinical_stage_state");
        ConfigureStageProperty(builder.Property(operation => operation.IdentityStageState), "identity_stage_state");

        builder.Property(operation => operation.InfrastructureResourceRef)
            .HasColumnName("infrastructure_resource_ref")
            .HasMaxLength(TenantProvisioningOperation.TechnicalReferenceMaxLength);

        builder.Property(operation => operation.ClinicalMigrationVersion)
            .HasColumnName("clinical_migration_version")
            .HasMaxLength(TenantProvisioningOperation.TechnicalReferenceMaxLength);

        builder.Property(operation => operation.InitialAdminAccountId)
            .HasColumnName("initial_admin_account_id")
            .HasMaxLength(TenantProvisioningOperation.TechnicalReferenceMaxLength);

        builder.HasOne<Tenant>()
            .WithOne()
            .HasForeignKey<TenantProvisioningOperation>(operation => operation.TenantId)
            .HasConstraintName("fk_tenant_provisioning_operations_tenants_tenant_id")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(operation => operation.TenantId)
            .IsUnique()
            .HasDatabaseName("ux_tenant_provisioning_operations_tenant_id");

        builder.HasIndex(operation => operation.ProvisioningRequestId)
            .IsUnique()
            .HasDatabaseName("ux_tenant_provisioning_operations_provisioning_request_id");

        builder.HasIndex(operation => new { operation.Status, operation.CreatedAtUtc })
            .HasDatabaseName("ix_tenant_provisioning_operations_status_created_at_utc");

        builder.HasIndex(operation => new
        {
            operation.Status,
            operation.ManualInterventionRequired,
            operation.NextAttemptAtUtc,
            operation.LeaseUntilUtc
        })
            .HasDatabaseName("ix_tenant_provisioning_operations_worker_acquisition");
    }

    private static void ConfigureStageProperty(
        PropertyBuilder<ProvisioningStageState> propertyBuilder,
        string columnName)
    {
        propertyBuilder
            .HasColumnName(columnName)
            .HasMaxLength(TenantProvisioningOperation.StageStateMaxLength)
            .HasConversion(
                state => state.ToCode(),
                code => ProvisioningStageStateExtensions.ParseCode(code))
            .HasDefaultValue(ProvisioningStageState.Pending)
            .ValueGeneratedNever()
            .IsRequired();
    }
}
