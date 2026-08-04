using SeatReservation.Domain.Common;

namespace SeatReservation.Domain.Entities;

public enum SeatStatus
{
    Available = 0,
    Held = 1,
    Booked = 2
}

/// <summary>
/// One numbered seat for one event.
///
/// Status transitions go through the methods below rather than through a public setter,
/// so "is this seat free?" and "take this seat" cannot be separated by a caller — which
/// is exactly the gap two concurrent requests slip through.
/// </summary>
public sealed class Seat
{
    private Seat() { } // EF Core

    private Seat(Guid id, Guid eventId, string row, int number, decimal price)
    {
        Id = id;
        EventId = eventId;
        Row = row;
        Number = number;
        Price = price;
        Status = SeatStatus.Available;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string Row { get; private set; } = string.Empty;
    public int Number { get; private set; }
    public decimal Price { get; private set; }
    public SeatStatus Status { get; private set; }
    public Guid? ReservationId { get; private set; }

    /// <summary>
    /// PostgreSQL's system column, mapped as a concurrency token. Every UPDATE carries the
    /// row version it read; if another transaction changed the row in between, zero rows
    /// match and EF Core raises DbUpdateConcurrencyException instead of overwriting.
    /// </summary>
    public uint Version { get; private set; }

    public static Seat Create(Guid eventId, string row, int number, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(row);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentOutOfRangeException.ThrowIfNegative(price);

        return new Seat(Guid.NewGuid(), eventId, row.Trim().ToUpperInvariant(), number, price);
    }

    public Result Hold(Guid reservationId)
    {
        if (Status != SeatStatus.Available)
            return Result.Failure(DomainErrors.Seat.NotAvailable);

        Status = SeatStatus.Held;
        ReservationId = reservationId;
        return Result.Success();
    }

    public Result Book()
    {
        if (Status != SeatStatus.Held)
            return Result.Failure(DomainErrors.Seat.NotHeld);

        Status = SeatStatus.Booked;
        return Result.Success();
    }

    /// <summary>Returns the seat to the pool. Idempotent: releasing a free seat is not an error.</summary>
    public void Release()
    {
        Status = SeatStatus.Available;
        ReservationId = null;
    }

    public string Label => $"{Row}{Number}";
}
