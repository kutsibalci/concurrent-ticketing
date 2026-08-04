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
