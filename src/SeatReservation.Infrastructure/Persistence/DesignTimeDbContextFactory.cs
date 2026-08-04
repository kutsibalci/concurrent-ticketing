using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SeatReservation.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only.
///
/// Without it the tools build the API host to find the context, which runs the startup
/// options validation — so adding a migration would require a JWT signing key and a Redis
/// endpoint that have nothing to do with the schema. The connection string here is never
/// opened for <c>migrations add</c>; only the provider matters, so the generated SQL is
/// PostgreSQL.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=seatreservation;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
