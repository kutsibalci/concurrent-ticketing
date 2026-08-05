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

        // The rate limiter partitions on the caller's address, and every test here arrives
        // from the same one -- so the production limit of ten a minute would be spent by
        // the third test and every later registration would come back 429. Raised out of
        // the way, and the limit itself is proven in RateLimitTests against a host
        // configured with a low one.
        builder.UseSetting("RateLimiting:PermitLimit", "10000");
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

    /// <summary>
    /// Wipes every table between tests so one test's rows cannot decide another's outcome.
    ///
    /// Listing tables by hand means a new table is silently missed — which is exactly what
    /// happened when the outbox was added, and it surfaced as unrelated tests failing on
    /// rows an earlier one had left behind. Read from the model instead, so a table added
    /// later is truncated without anyone having to remember this method exists.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        await using var db = CreateDbContext();

        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Distinct()
            .Select(name => $"\"{name}\"");

        // EF1002 warns about interpolation into raw SQL. A table name cannot be a
        // parameter, and these come from the compiled model's metadata rather than from
        // anything a caller supplies, so there is no input here to inject through.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE;");
#pragma warning restore EF1002
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFactory>;
