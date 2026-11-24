using Microsoft.EntityFrameworkCore;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Persistence.Configurations;

namespace Saas.Platform.Api.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string SchemaName = "platform";
    public const string MigrationsHistoryTableName = "__ef_migrations_history";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantProvisioningOperation> TenantProvisioningOperations => Set<TenantProvisioningOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new TenantProvisioningOperationConfiguration());
    }
}
