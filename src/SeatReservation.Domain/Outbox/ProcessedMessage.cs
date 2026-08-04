namespace SeatReservation.Domain.Outbox;

/// <summary>
/// A message this consumer has already handled.
///
/// The outbox guarantees at-least-once, not exactly-once: a dispatcher that publishes and
/// then dies before committing will publish again, and RabbitMQ redelivers anything not
/// acknowledged. So the consumer has to be the thing that makes a repeat harmless.
///
/// The primary key is the message id, and the insert happens in the same transaction as
/// the work. A duplicate delivery violates the key, which is the signal to acknowledge and
/// move on — the database decides, not a check that another delivery could race past.
/// </summary>
public sealed class ProcessedMessage
{
    private ProcessedMessage() { } // EF Core

    private ProcessedMessage(Guid id, string consumer, string type, DateTimeOffset processedAt)
    {
        Id = id;
        Consumer = consumer;
        Type = type;
        ProcessedAt = processedAt;
    }

    /// <summary>The broker's message id, which is the outbox row id.</summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Which consumer handled it. Part of the key so a second consumer added later gets
    /// its own copy of every message rather than skipping what this one already saw.
    /// </summary>
    public string Consumer { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedMessage Create(Guid id, string consumer, string type, DateTimeOffset processedAt)
        => new(id, consumer, type, processedAt);
}
