using Microsoft.EntityFrameworkCore;
using SeatReservation.Domain.Entities;

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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="action"/> inside a transaction, retrying on a transient failure.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
