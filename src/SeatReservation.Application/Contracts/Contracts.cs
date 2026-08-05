using System.ComponentModel.DataAnnotations;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Application.Contracts;

// ---------------------------------------------------------------------- auth

public sealed record RegisterRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MinLength(10), MaxLength(128)] string Password,
    [property: Required, MaxLength(120)] string DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public sealed record RefreshRequest([property: Required] string RefreshToken);

public sealed record AuthResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt);

// ---------------------------------------------------------------- pagination

/// <summary>
/// Query parameters for a paged list. The ceiling is on the type rather than left to each
/// endpoint, so a new list cannot be added without one.
/// </summary>
public sealed record PageRequest
{
    /// <summary>
    /// The most rows one request may ask for. Without a ceiling, <c>?size=1000000</c> is a
    /// request to read the whole table into memory and serialise it, which is a denial of
    /// service anyone can send.
    /// </summary>
    public const int MaxSize = 100;

    public const int DefaultSize = 20;

    // Nullable, and the defaults live below rather than in property initialisers.
    //
    // [AsParameters] binding does not run initialisers: it constructs the type and assigns
    // each property from the query string, so an absent `page` arrives as default(int) --
    // zero -- and not as the 1 an initialiser would have set. With a non-nullable int and
    // [Range(1, ...)] that made a request with no query string at all fail validation, which
    // is every existing caller of this endpoint.
    //
    // Nullable separates "not supplied" from "supplied as zero". RangeAttribute passes null
    // through untouched, so an omitted value takes the default while an explicit ?page=0 is
    // still a 400.
    [Range(1, int.MaxValue)]
    public int? Page { get; init; }

    [Range(1, MaxSize)]
    public int? Size { get; init; }

    public int PageOrDefault => Page ?? 1;

    public int SizeOrDefault => Size ?? DefaultSize;
}

/// <summary>
/// A page of results and enough context to ask for the next one. <see cref="TotalCount"/> is
/// the count before paging, so a client can tell "no more pages" from "no more matches".
/// </summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int Size,
    int TotalCount)
{
    public int TotalPages => Size <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)Size);

    public bool HasNextPage => Page < TotalPages;
}

// -------------------------------------------------------------------- events

public sealed record EventSummaryResponse(
    Guid Id,
    string Name,
    string Venue,
    DateTimeOffset StartsAt,
    bool SalesOpen,
    int TotalSeats,
    int AvailableSeats);

public sealed record SeatResponse(Guid Id, string Row, int Number, string Label, decimal Price, string Status);

public sealed record SeatMapResponse(
    Guid EventId,
    string EventName,
    DateTimeOffset StartsAt,
    IReadOnlyList<SeatResponse> Seats);

public sealed record CreateEventRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(200)] string Venue,
    [property: Required] DateTimeOffset StartsAt,
    [property: Required, MinLength(1)] IReadOnlyList<SeatBlockRequest> SeatBlocks);

public sealed record SeatBlockRequest(
    [property: Required, MaxLength(4)] string Row,
    [property: Range(1, 500)] int FromNumber,
    [property: Range(1, 500)] int ToNumber,
    [property: Range(0, 1_000_000)] decimal Price);

// -------------------------------------------------------------- reservations

public sealed record CreateReservationRequest(
    [property: Required] Guid EventId,
    [property: Required, MinLength(1), MaxLength(Reservation.MaxSeatsPerReservation)] IReadOnlyList<Guid> SeatIds);

public sealed record ReservationResponse(
    Guid Id,
    Guid EventId,
    string EventName,
    string Status,
    decimal TotalPrice,
    DateTimeOffset CreatedAt,
    DateTimeOffset HoldExpiresAt,
    DateTimeOffset? ConfirmedAt,
    IReadOnlyList<SeatResponse> Seats)
{
    /// <summary>Seconds left on the hold, so a client can show a countdown without doing clock arithmetic against the server.</summary>
    public int? SecondsUntilExpiry { get; init; }
}
