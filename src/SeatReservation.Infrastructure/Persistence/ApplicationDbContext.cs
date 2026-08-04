using Microsoft.EntityFrameworkCore;
using SeatReservation.Application.Abstractions;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Runs work inside a transaction using EF Core's execution strategy.
    ///
    /// The strategy retries on transient failures, and a retry replays the whole delegate —
    /// so the transaction has to be created inside it. Opening the transaction outside is
    /// the classic mistake here, and EF throws rather than silently retrying half of one.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        var strategy = Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await Database.BeginTransactionAsync(ct);

            var result = await action(ct);

            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}
