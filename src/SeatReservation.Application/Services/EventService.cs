using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Options;
using SeatReservation.Domain.Common;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Application.Services;

public sealed class EventService
{
    private readonly IApplicationDbContext _db;
    private readonly ISeatAvailabilityCache _cache;
    private readonly ReservationOptions _options;

    public EventService(
        IApplicationDbContext db, ISeatAvailabilityCache cache, IOptions<ReservationOptions> options)
    {
        _db = db;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<PagedResponse<EventSummaryResponse>> ListAsync(
        PageRequest request, CancellationToken ct = default)
    {
        // Clamped here as well as validated at the endpoint. Validation is what answers a
        // caller with a 400; this is what still holds if the method is called from anywhere
        // that is not an HTTP request, which is the case a ceiling exists for.
        var size = Math.Clamp(request.SizeOrDefault, 1, PageRequest.MaxSize);
        var page = Math.Max(1, request.PageOrDefault);

        var query = _db.Events.AsNoTracking();

        // Counted before paging, so a client can tell an empty page from an empty catalogue
        // without having to ask twice.
        var totalCount = await query.CountAsync(ct);

        var items = await query
            // ThenBy(Id) is not decoration. Two events starting at the same moment have no
            // defined order between them, and OFFSET/LIMIT asks the database for a slice of
            // an ordering it is free to pick differently on the next query -- so the same
            // row can arrive on page one and again on page two, or on neither. A unique
            // tiebreaker makes the ordering total, and a total ordering is what makes
            // paging through it safe.
            .OrderBy(e => e.StartsAt).ThenBy(e => e.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => new EventSummaryResponse(
                e.Id,
                e.Name,
                e.Venue,
                e.StartsAt,
                e.SalesOpen,
                e.Seats.Count,
                // Counted in SQL. Loading the seats to count them in memory is the
                // difference between one aggregate and thousands of rows per event.
                e.Seats.Count(s => s.Status == SeatStatus.Available)))
            .ToListAsync(ct);

        return new PagedResponse<EventSummaryResponse>(items, page, size, totalCount);
    }

    public async Task<Result<SeatMapResponse>> GetSeatMapAsync(Guid eventId, CancellationToken ct = default)
    {
        var key = ISeatAvailabilityCache.SeatMapKey(eventId);

        var cached = await _cache.GetAsync<SeatMapResponse>(key, ct);
        if (cached is not null)
            return cached;

        var @event = await _db.Events
            .AsNoTracking()
            .Include(e => e.Seats)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (@event is null)
            return Result.Failure<SeatMapResponse>(DomainErrors.Event.NotFound);

        var response = new SeatMapResponse(
            @event.Id,
            @event.Name,
            @event.StartsAt,
            @event.Seats
                .OrderBy(s => s.Row).ThenBy(s => s.Number)
                .Select(s => new SeatResponse(s.Id, s.Row, s.Number, s.Label, s.Price, s.Status.ToString()))
                .ToList());

        // Short TTL rather than a long one with clever invalidation. The seat map is read
        // constantly and every write path already evicts this key; the TTL is only a
        // backstop for an eviction that somehow did not happen.
        await _cache.SetAsync(key, response, _options.SeatMapCacheTtl, ct);

        return response;
    }

    public async Task<Result<EventSummaryResponse>> CreateAsync(
        CreateEventRequest request, CancellationToken ct = default)
    {
        var @event = Event.Create(request.Name, request.Venue, request.StartsAt);

        var seatCount = 0;
        foreach (var block in request.SeatBlocks)
        {
            if (block.FromNumber > block.ToNumber)
                return Result.Failure<EventSummaryResponse>(
                    new Error("event.invalid_seat_block", $"'{block.Row}' sırasında başlangıç numarası bitişten büyük."));

            seatCount += @event.AddSeatBlock(block.Row, block.FromNumber, block.ToNumber, block.Price).Count;
        }

        _db.Events.Add(@event);
        await _db.SaveChangesAsync(ct);

        return new EventSummaryResponse(
            @event.Id, @event.Name, @event.Venue, @event.StartsAt, @event.SalesOpen, seatCount, seatCount);
    }
}
