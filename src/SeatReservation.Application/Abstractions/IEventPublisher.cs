namespace SeatReservation.Application.Abstractions;

/// <summary>
/// Publishes a message to the broker.
///
/// Behind an interface so the dispatcher can be tested — including its failure and retry
/// paths — without a broker, and so swapping RabbitMQ for something else does not reach
/// into the outbox logic.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes and waits for the broker to acknowledge it.
    ///
    /// Waiting matters: a fire-and-forget publish returns before the broker has the
    /// message, so the outbox row would be marked processed for something that may never
    /// have arrived. Throws on failure — the dispatcher decides what a failure means.
    /// </summary>
    Task PublishAsync(string messageId, string type, string payload, CancellationToken cancellationToken = default);
}
