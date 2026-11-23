using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saas.Platform.Api.Persistence;

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string DesignTimeFallbackConnectionString =
        "Host=localhost;Port=5432;Database=saas_platform_design;Username=postgres";

    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PlatformDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DesignTimeFallbackConnectionString;
        }

        var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                PlatformDbContext.MigrationsHistoryTableName,
                PlatformDbContext.SchemaName));

        return new PlatformDbContext(optionsBuilder.Options);
    }
}
