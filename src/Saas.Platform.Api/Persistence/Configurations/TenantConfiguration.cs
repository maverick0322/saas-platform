using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", PlatformDbContext.SchemaName, tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_tenants_public_id_length",
                $"char_length(btrim(public_id)) >= {Tenant.PublicIdMinLength}");

            tableBuilder.HasCheckConstraint(
                "ck_tenants_name_length",
                $"char_length(btrim(name)) >= {Tenant.NameMinLength}");

            tableBuilder.HasCheckConstraint(
                "ck_tenants_contact_email_not_empty",
                "char_length(btrim(contact_email)) >= 3");
        });

        builder.HasKey(tenant => tenant.Id)
            .HasName("pk_tenants");

        builder.Property(tenant => tenant.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(tenant => tenant.PublicId)
            .HasColumnName("public_id")
            .HasMaxLength(Tenant.PublicIdMaxLength)
            .IsRequired();

        builder.Property(tenant => tenant.Name)
            .HasColumnName("name")
            .HasMaxLength(Tenant.NameMaxLength)
            .IsRequired();

        builder.Property(tenant => tenant.ContactEmail)
            .HasColumnName("contact_email")
            .HasMaxLength(Tenant.ContactEmailMaxLength)
            .IsRequired();

        builder.Property(tenant => tenant.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();

        builder.Property(tenant => tenant.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .IsRequired();

        builder.Property(tenant => tenant.ConcurrencyStamp)
            .HasColumnName("concurrency_stamp")
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(tenant => tenant.PublicId)
            .IsUnique()
            .HasDatabaseName("ux_tenants_public_id");
    }
}
