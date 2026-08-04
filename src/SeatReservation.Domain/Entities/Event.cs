using SeatReservation.Domain.Common;

namespace SeatReservation.Domain.Entities;

public sealed class Event
{
    private readonly List<Seat> _seats = [];

    private Event() { } // EF Core

    private Event(Guid id, string name, string venue, DateTimeOffset startsAt)
    {
        Id = id;
        Name = name;
        Venue = venue;
        StartsAt = startsAt;
        SalesOpen = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Venue { get; private set; } = string.Empty;
    public DateTimeOffset StartsAt { get; private set; }
    public bool SalesOpen { get; private set; }

    public IReadOnlyCollection<Seat> Seats => _seats.AsReadOnly();

    public static Event Create(string name, string venue, DateTimeOffset startsAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);

        return new Event(Guid.NewGuid(), name.Trim(), venue.Trim(), startsAt);
    }

    /// <summary>Lays out a rectangular block of seats. Used by seeding and by the admin endpoint.</summary>
    public IReadOnlyList<Seat> AddSeatBlock(string row, int fromNumber, int toNumber, decimal price)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fromNumber, toNumber);

        var created = new List<Seat>();
        for (var number = fromNumber; number <= toNumber; number++)
        {
            var seat = Seat.Create(Id, row, number, price);
            _seats.Add(seat);
            created.Add(seat);
        }

        return created;
    }

    public void CloseSales() => SalesOpen = false;

    /// <summary>
    /// Whether the event can still take reservations. Checked against an injected clock
    /// rather than DateTimeOffset.Now so a test can place itself either side of the start.
    /// </summary>
    public Result EnsureBookable(DateTimeOffset now)
    {
        if (!SalesOpen)
            return Result.Failure(DomainErrors.Event.SalesClosed);

        return now >= StartsAt
            ? Result.Failure(DomainErrors.Event.AlreadyStarted)
            : Result.Success();
    }
}
