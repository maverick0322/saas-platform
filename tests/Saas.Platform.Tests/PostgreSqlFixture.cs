using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saas.Platform.Api.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("saas_platform_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public PlatformDbContext CreateDbContext(string? connectionString = null)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(
                connectionString ?? ConnectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    PlatformDbContext.MigrationsHistoryTableName,
                    PlatformDbContext.SchemaName))
            .Options;

        return new PlatformDbContext(options);
    }

    public async Task<string> CreateEmptyDatabaseAsync(string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName
        };

        return builder.ConnectionString;
    }
}
