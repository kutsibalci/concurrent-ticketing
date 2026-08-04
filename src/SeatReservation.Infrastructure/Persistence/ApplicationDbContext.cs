using Microsoft.EntityFrameworkCore;
using SeatReservation.Application.Abstractions;
using SeatReservation.Domain.Entities;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// <c>FOR UPDATE SKIP LOCKED</c> is what makes more than one dispatcher safe.
    ///
    /// <c>FOR UPDATE</c> alone would make a second dispatcher wait for the first, turning
    /// horizontal scale into a queue. <c>SKIP LOCKED</c> tells PostgreSQL to pass over
    /// rows another transaction already holds, so each dispatcher claims a disjoint batch
    /// and they genuinely work in parallel — with no row published twice.
    ///
    /// Column names are quoted because the model maps them in PascalCase.
    /// </summary>
    private const string ClaimDueOutboxSql = """
        SELECT * FROM outbox_messages
        WHERE "ProcessedAt" IS NULL
          AND "IsDead" = false
          AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {0})
        ORDER BY "OccurredAt"
        LIMIT {1}
        FOR UPDATE SKIP LOCKED
        """;

    public Task<List<OutboxMessage>> ClaimDueOutboxMessagesAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
        => OutboxMessages
            .FromSqlRaw(ClaimDueOutboxSql, now, batchSize)
            .ToListAsync(cancellationToken);

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
