using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API against a real PostgreSQL in a container.
///
/// A container rather than an in-memory provider because the behaviour under test *is*
/// PostgreSQL behaviour: the concurrency token is the <c>xmin</c> system column, and no
/// fake provider has one. A test that cannot lose a race cannot prove the race is handled.
/// </summary>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The image is pinned rather than left to float: a test that silently changes
    // database version between runs is a test whose failures are hard to reproduce.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("seatreservation")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", string.Empty); // in-memory cache
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-chars");
        builder.UseSetting("Reservation:HoldDuration", "00:10:00");
        // Long enough that the sweeper never fires mid-test; expiry is exercised directly.
        builder.UseSetting("Reservation:SweepInterval", "01:00:00");
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    public ApplicationDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    /// <summary>Wipes every table between tests so one test's rows cannot decide another's outcome.</summary>
    public async Task ResetDatabaseAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE refresh_tokens, reservations, seats, events, users RESTART IDENTITY CASCADE;
            """);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFactory>;
