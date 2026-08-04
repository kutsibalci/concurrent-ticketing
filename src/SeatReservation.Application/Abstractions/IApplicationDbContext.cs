using Microsoft.EntityFrameworkCore;
using SeatReservation.Domain.Entities;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Application.Abstractions;

/// <summary>
/// The persistence surface the application layer is allowed to see. Keeps the use cases
/// compiling against an interface rather than a concrete DbContext, so they can be tested
/// against any provider and the EF Core dependency stays in Infrastructure.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Event> Events { get; }
    DbSet<Seat> Seats { get; }
    DbSet<Reservation> Reservations { get; }
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }

    /// <summary>
    /// Events queued for publication. Written in the same transaction as the change that
    /// produced them, which is the whole point — see <see cref="OutboxMessage"/>.
    /// </summary>
    DbSet<OutboxMessage> OutboxMessages { get; }

    /// <summary>Delivery receipts, used to make repeated deliveries harmless.</summary>
    DbSet<ProcessedMessage> ProcessedMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="action"/> inside a transaction, retrying on a transient failure.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a batch of outbox rows that are due, locking them against other dispatchers.
    ///
    /// Declared here rather than written in the dispatcher because the implementation is
    /// <c>FOR UPDATE SKIP LOCKED</c> — PostgreSQL-specific SQL that has no business in the
    /// application layer. Must be called inside a transaction; the locks are held until it
    /// commits.
    /// </summary>
    Task<List<OutboxMessage>> ClaimDueOutboxMessagesAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);
}
