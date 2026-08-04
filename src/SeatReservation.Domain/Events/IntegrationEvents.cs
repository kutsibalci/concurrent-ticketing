namespace SeatReservation.Domain.Events;

/// <summary>
/// Marker for events that leave this service.
///
/// These are a published contract, not internal shapes: once a consumer reads a field,
/// removing it breaks them. Kept as flat records with primitive members for that reason —
/// no entities, no navigation properties, nothing that drags the domain model across the
/// wire.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Stable name used as the routing key and to pick a deserialiser on the consumer side.</summary>
    static abstract string EventType { get; }
}

public sealed record ReservationConfirmedEvent(
    Guid ReservationId,
    Guid EventId,
    Guid UserId,
    string UserEmail,
    string EventName,
    decimal TotalPrice,
    IReadOnlyList<string> SeatLabels,
    DateTimeOffset ConfirmedAt) : IIntegrationEvent
{
    public static string EventType => "reservation.confirmed";
}

public sealed record ReservationCancelledEvent(
    Guid ReservationId,
    Guid EventId,
    Guid UserId,
    string UserEmail,
    IReadOnlyList<string> SeatLabels,
    DateTimeOffset CancelledAt) : IIntegrationEvent
{
    public static string EventType => "reservation.cancelled";
}

public sealed record ReservationExpiredEvent(
    Guid ReservationId,
    Guid EventId,
    Guid UserId,
    string UserEmail,
    IReadOnlyList<string> SeatLabels,
    DateTimeOffset ExpiredAt) : IIntegrationEvent
{
    public static string EventType => "reservation.expired";
}
