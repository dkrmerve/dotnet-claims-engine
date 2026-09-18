using ClaimsEngine.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ClaimsEngine.Api.Tests.Support;

/// <summary>
/// One PostgreSQL container (Testcontainers) per test run, shared through an xUnit collection
/// fixture. Every test gets its own freshly created database inside it, so tests never share
/// state and can run in any order. Set CLAIMS_TESTS_DB=sqlite to use in-memory SQLite instead
/// (fast profile used inside "docker build", where no Docker daemon is available).
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string ProviderVariable = "CLAIMS_TESTS_DB";
    public const string PostgresImage = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    public static bool UsePostgres =>
        !string.Equals(Environment.GetEnvironmentVariable(ProviderVariable), "sqlite", StringComparison.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        if (UsePostgres)
        {
            _container = new PostgreSqlBuilder(PostgresImage).Build();
            await _container.StartAsync();
        }
    }

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        if (!UsePostgres)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            return new TestDatabase(options => options.UseSqlite(connection), connection, null);
        }

        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            MinPoolSize = 1,
            MaxPoolSize = 20,
            Timeout = 15,
            CommandTimeout = 30,
        }.ConnectionString;

        return new TestDatabase(options => DependencyInjection.ConfigureNpgsql(options, connectionString), null, connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>A database handed to one test: how to configure EF for it, and how to clean up.</summary>
public sealed class TestDatabase(Action<DbContextOptionsBuilder> configure, SqliteConnection? sqlite, string? connectionString) : IAsyncDisposable
{
    public bool IsPostgres => connectionString is not null;

    public string? ConnectionString => connectionString;

    public void Configure(DbContextOptionsBuilder options) => configure(options);

    public async ValueTask DisposeAsync()
    {
        if (sqlite is not null)
        {
            await sqlite.DisposeAsync();
        }
        else
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "api";
}

/// <summary>A fact that only runs against real PostgreSQL (parallelism, xmin, constraints).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!DatabaseFixture.UsePostgres)
        {
            Skip = $"Requires PostgreSQL (unset {DatabaseFixture.ProviderVariable}=sqlite).";
        }
    }
}
