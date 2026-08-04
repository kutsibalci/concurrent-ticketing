using SeatReservation.Domain.Common;

namespace SeatReservation.Domain.Entities;

public enum ReservationStatus
{
    /// <summary>Seats are held; the hold lapses at <see cref="Reservation.HoldExpiresAt"/>.</summary>
    Pending = 0,
    Confirmed = 1,
    Cancelled = 2,
    Expired = 3
}

/// <summary>
/// A hold over one or more seats, which becomes a booking when confirmed in time.
///
/// The two-step hold exists because taking payment is slow. Marking seats Booked only
/// after payment would let a second customer take them mid-checkout; marking them Booked
/// before would strand them for good if the customer walks away. A hold with an expiry
/// is the middle ground, and the expiry is what the sweeper reclaims.
/// </summary>
public sealed class Reservation
{
    private readonly List<Seat> _seats = [];

    private Reservation() { } // EF Core

    private Reservation(
        Guid id, Guid eventId, Guid userId, DateTimeOffset createdAt, DateTimeOffset holdExpiresAt)
    {
        Id = id;
        EventId = eventId;
        UserId = userId;
        CreatedAt = createdAt;
        HoldExpiresAt = holdExpiresAt;
        Status = ReservationStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid UserId { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset HoldExpiresAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public decimal TotalPrice { get; private set; }

    public IReadOnlyCollection<Seat> Seats => _seats.AsReadOnly();

    public const int MaxSeatsPerReservation = 8;

    public static Result<Reservation> Create(
        Event @event,
        Guid userId,
        IReadOnlyList<Seat> seats,
        DateTimeOffset now,
        TimeSpan holdDuration)
    {
        var bookable = @event.EnsureBookable(now);
        if (bookable.IsFailure)
            return Result.Failure<Reservation>(bookable.Error);

        if (seats.Count == 0)
            return Result.Failure<Reservation>(DomainErrors.Reservation.NoSeats);

        if (seats.Count > MaxSeatsPerReservation)
            return Result.Failure<Reservation>(DomainErrors.Reservation.TooManySeats);

        if (seats.Select(s => s.Id).Distinct().Count() != seats.Count)
            return Result.Failure<Reservation>(DomainErrors.Reservation.DuplicateSeats);

        if (seats.Any(s => s.EventId != @event.Id))
            return Result.Failure<Reservation>(DomainErrors.Seat.WrongEvent);

        var reservation = new Reservation(
            Guid.NewGuid(), @event.Id, userId, now, now.Add(holdDuration));

        foreach (var seat in seats)
        {
            var held = seat.Hold(reservation.Id);
            if (held.IsFailure)
                return Result.Failure<Reservation>(held.Error);

            reservation._seats.Add(seat);
        }

        reservation.TotalPrice = seats.Sum(s => s.Price);
        return reservation;
    }

    public bool HasExpired(DateTimeOffset now)
        => Status == ReservationStatus.Pending && now >= HoldExpiresAt;

    public Result Confirm(DateTimeOffset now)
    {
        if (Status == ReservationStatus.Confirmed)
            return Result.Failure(DomainErrors.Reservation.AlreadyConfirmed);

        if (Status != ReservationStatus.Pending)
            return Result.Failure(DomainErrors.Reservation.NotPending);

        // Checked here rather than relying on the sweeper having already run: the
        // background job is a cleanup, not the thing that enforces the deadline.
        if (HasExpired(now))
            return Result.Failure(DomainErrors.Reservation.Expired);

        foreach (var seat in _seats)
        {
            var booked = seat.Book();
            if (booked.IsFailure)
                return Result.Failure(booked.Error);
        }

        Status = ReservationStatus.Confirmed;
        ConfirmedAt = now;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status is ReservationStatus.Cancelled or ReservationStatus.Expired)
            return Result.Success(); // idempotent

        foreach (var seat in _seats)
            seat.Release();

        Status = ReservationStatus.Cancelled;
        CancelledAt = now;
        return Result.Success();
    }

    /// <summary>Reclaims a lapsed hold. Called by the sweeper, not by a user action.</summary>
    public Result Expire(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Pending)
            return Result.Failure(DomainErrors.Reservation.NotPending);

        if (!HasExpired(now))
            return Result.Failure(DomainErrors.Reservation.NotPending);

        foreach (var seat in _seats)
            seat.Release();

        Status = ReservationStatus.Expired;
        return Result.Success();
    }

    public Result EnsureOwnedBy(Guid userId)
        => UserId == userId
            ? Result.Success()
            : Result.Failure(DomainErrors.Reservation.NotOwner);
}
