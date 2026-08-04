using System.Text.Json;
using SeatReservation.Domain.Events;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Application.Services;

public static class OutboxWriter
{
    /// <summary>
    /// Web defaults so the JSON the consumer receives is camelCase, matching the API's own
    /// responses. The serialiser settings are part of the published contract too.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Turns an event into an outbox row. The caller adds it to the change tracker so it
    /// is saved by the same <c>SaveChanges</c> as the change that produced it — separate
    /// calls would leave a window where one landed and the other did not.
    /// </summary>
    public static OutboxMessage For<TEvent>(TEvent @event, DateTimeOffset occurredAt)
        where TEvent : IIntegrationEvent
        => OutboxMessage.Create(
            TEvent.EventType,
            JsonSerializer.Serialize(@event, SerializerOptions),
            occurredAt);
}
