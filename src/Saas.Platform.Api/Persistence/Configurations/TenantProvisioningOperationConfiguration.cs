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
    }
}
