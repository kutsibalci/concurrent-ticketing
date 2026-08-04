using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Options;
using SeatReservation.Domain.Common;
using SeatReservation.Domain.Entities;
using SeatReservation.Domain.Events;

namespace SeatReservation.Application.Services;

/// <summary>
/// The reservation flow, and the only place seat status changes.
///
/// The interesting problem is that two customers can click the same seat in the same
/// millisecond. Reading the seat, seeing Available and then writing Held is a
/// time-of-check/time-of-use race: both reads succeed, both writes land, and the seat is
/// sold twice. This is prevented by a concurrency token on the seat row rather than by
/// locking the table — see <see cref="Seat.Version"/>.
/// </summary>
public sealed class ReservationService
{
    private readonly IApplicationDbContext _db;
    private readonly ISeatAvailabilityCache _cache;
    private readonly TimeProvider _clock;
    private readonly ReservationOptions _options;
    private readonly ILogger<ReservationService> _logger;

    public ReservationService(
        IApplicationDbContext db,
        ISeatAvailabilityCache cache,
        TimeProvider clock,
        IOptions<ReservationOptions> options,
        ILogger<ReservationService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<ReservationResponse>> CreateAsync(
        Guid userId, CreateReservationRequest request, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var @event = await _db.Events
            .FirstOrDefaultAsync(e => e.Id == request.EventId, ct);

        if (@event is null)
            return Result.Failure<ReservationResponse>(DomainErrors.Event.NotFound);

        var seatIds = request.SeatIds.Distinct().ToList();
        if (seatIds.Count != request.SeatIds.Count)
            return Result.Failure<ReservationResponse>(DomainErrors.Reservation.DuplicateSeats);

        var seats = await _db.Seats
            .Where(s => seatIds.Contains(s.Id))
            // Deterministic order. Two requests grabbing overlapping seats in opposite
            // orders can deadlock at the database; ordering makes them queue instead.
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        if (seats.Count != seatIds.Count)
            return Result.Failure<ReservationResponse>(DomainErrors.Seat.NotFound);

        var reservationResult = Reservation.Create(@event, userId, seats, now, _options.HoldDuration);
        if (reservationResult.IsFailure)
            return Result.Failure<ReservationResponse>(reservationResult.Error);

        var reservation = reservationResult.Value;
        _db.Reservations.Add(reservation);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else's UPDATE landed between our read and our write. Their version
            // no longer matches ours, so zero rows were affected and EF raised this rather
            // than overwriting their hold. The seat is theirs; we report the conflict.
            _logger.LogInformation(
                "Rezervasyon cakismasi: kullanici {UserId}, etkinlik {EventId}", userId, request.EventId);

            return Result.Failure<ReservationResponse>(DomainErrors.Reservation.ConcurrencyConflict);
        }

        await _cache.RemoveAsync(ISeatAvailabilityCache.SeatMapKey(@event.Id), ct);

        _logger.LogInformation(
            "Rezervasyon {ReservationId} olusturuldu: {SeatCount} koltuk, kullanici {UserId}",
            reservation.Id, seats.Count, userId);

        return ToResponse(reservation, @event, now);
    }

    public async Task<Result<ReservationResponse>> ConfirmAsync(
        Guid reservationId, Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var reservation = await LoadWithSeatsAsync(reservationId, ct);
        if (reservation is null)
            return Result.Failure<ReservationResponse>(DomainErrors.Reservation.NotFound);

        var owned = reservation.EnsureOwnedBy(userId);
        if (owned.IsFailure)
            return Result.Failure<ReservationResponse>(owned.Error);

        var confirmed = reservation.Confirm(now);
        if (confirmed.IsFailure)
            return Result.Failure<ReservationResponse>(confirmed.Error);

        var @event = await _db.Events.FirstAsync(e => e.Id == reservation.EventId, ct);
        var email = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstAsync(ct);

        // Queued in the change tracker, not published here. It is saved by the same
        // SaveChanges as the confirmation below, so the event and the state change are one
        // atomic write — a publish at this point could succeed for a commit that then fails.
        _db.OutboxMessages.Add(OutboxWriter.For(
            new ReservationConfirmedEvent(
                reservation.Id,
                reservation.EventId,
                userId,
                email,
                @event.Name,
                reservation.TotalPrice,
                reservation.Seats.Select(s => s.Label).OrderBy(l => l).ToList(),
                now),
            now));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ReservationResponse>(DomainErrors.Reservation.ConcurrencyConflict);
        }

        await _cache.RemoveAsync(ISeatAvailabilityCache.SeatMapKey(reservation.EventId), ct);

        return ToResponse(reservation, @event, now);
    }

    public async Task<Result> CancelAsync(Guid reservationId, Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var reservation = await LoadWithSeatsAsync(reservationId, ct);
        if (reservation is null)
            return Result.Failure(DomainErrors.Reservation.NotFound);

        // The id alone is not authorization: without this check any signed-in user could
        // cancel anyone else's booking by guessing an id.
        var owned = reservation.EnsureOwnedBy(userId);
        if (owned.IsFailure)
            return owned;

        var seatLabels = reservation.Seats.Select(s => s.Label).OrderBy(l => l).ToList();

        var cancelled = reservation.Cancel(now);
        if (cancelled.IsFailure)
            return cancelled;

        var email = await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstAsync(ct);

        _db.OutboxMessages.Add(OutboxWriter.For(
            new ReservationCancelledEvent(
                reservation.Id, reservation.EventId, userId, email, seatLabels, now),
            now));

        await _db.SaveChangesAsync(ct);
        await _cache.RemoveAsync(ISeatAvailabilityCache.SeatMapKey(reservation.EventId), ct);

        return Result.Success();
    }

    public async Task<Result<ReservationResponse>> GetAsync(
        Guid reservationId, Guid userId, CancellationToken ct = default)
    {
        var reservation = await LoadWithSeatsAsync(reservationId, ct, tracking: false);
        if (reservation is null)
            return Result.Failure<ReservationResponse>(DomainErrors.Reservation.NotFound);

        var owned = reservation.EnsureOwnedBy(userId);
        if (owned.IsFailure)
            return Result.Failure<ReservationResponse>(owned.Error);

        var @event = await _db.Events.AsNoTracking().FirstAsync(e => e.Id == reservation.EventId, ct);
        return ToResponse(reservation, @event, _clock.GetUtcNow());
    }

    public async Task<IReadOnlyList<ReservationResponse>> ListForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var reservations = await _db.Reservations
            .AsNoTracking()
            .Include(r => r.Seats)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var eventIds = reservations.Select(r => r.EventId).Distinct().ToList();
        var events = await _db.Events
            .AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        return reservations.Select(r => ToResponse(r, events[r.EventId], now)).ToList();
    }

    /// <summary>
    /// Releases holds that have lapsed. Run by the background sweeper.
    /// Returns how many were reclaimed.
    /// </summary>
    public async Task<int> ExpireLapsedHoldsAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var lapsed = await _db.Reservations
            .Include(r => r.Seats)
            .Where(r => r.Status == ReservationStatus.Pending && r.HoldExpiresAt <= now)
            .ToListAsync(ct);

        if (lapsed.Count == 0)
            return 0;

        // Emails fetched in one query rather than per reservation — a sweep can cover
        // hundreds of rows and this is the difference between one round trip and hundreds.
        var userIds = lapsed.Select(r => r.UserId).Distinct().ToList();
        var emails = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        foreach (var reservation in lapsed)
        {
            var seatLabels = reservation.Seats.Select(s => s.Label).OrderBy(l => l).ToList();

            if (reservation.Expire(now).IsFailure)
                continue;

            _db.OutboxMessages.Add(OutboxWriter.For(
                new ReservationExpiredEvent(
                    reservation.Id,
                    reservation.EventId,
                    reservation.UserId,
                    emails.GetValueOrDefault(reservation.UserId, string.Empty),
                    seatLabels,
                    now),
                now));
        }

        await _db.SaveChangesAsync(ct);

        foreach (var eventId in lapsed.Select(r => r.EventId).Distinct())
            await _cache.RemoveAsync(ISeatAvailabilityCache.SeatMapKey(eventId), ct);

        _logger.LogInformation("{Count} suresi dolmus rezervasyon serbest birakildi.", lapsed.Count);
        return lapsed.Count;
    }

    private Task<Reservation?> LoadWithSeatsAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var query = _db.Reservations.Include(r => r.Seats).AsQueryable();
        if (!tracking) query = query.AsNoTracking();

        return query.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    private static ReservationResponse ToResponse(Reservation reservation, Event @event, DateTimeOffset now)
    {
        var secondsLeft = reservation.Status == ReservationStatus.Pending
            ? (int)Math.Max(0, (reservation.HoldExpiresAt - now).TotalSeconds)
            : (int?)null;

        return new ReservationResponse(
            reservation.Id,
            reservation.EventId,
            @event.Name,
            reservation.Status.ToString(),
            reservation.TotalPrice,
            reservation.CreatedAt,
            reservation.HoldExpiresAt,
            reservation.ConfirmedAt,
            reservation.Seats
                .OrderBy(s => s.Row).ThenBy(s => s.Number)
                .Select(s => new SeatResponse(s.Id, s.Row, s.Number, s.Label, s.Price, s.Status.ToString()))
                .ToList())
        {
            SecondsUntilExpiry = secondsLeft
        };
    }
}
