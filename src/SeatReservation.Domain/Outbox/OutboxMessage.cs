namespace SeatReservation.Domain.Outbox;

/// <summary>
/// An event waiting to be published, stored in the same database as the change that
/// produced it.
///
/// The problem this solves: confirming a reservation writes to PostgreSQL and publishes to
/// RabbitMQ, and there is no transaction spanning both. Publish first and the broker may
/// hold an event for a database write that then fails. Publish after the commit and the
/// process can die in between, losing the event with no trace. Either way the two systems
/// disagree and nothing in the code says so.
///
/// Writing the event as a row in the same transaction as the reservation makes it atomic:
/// both land or neither does. A separate process then moves rows to the broker, which
/// turns "exactly once" — which is not available — into "at least once", which is, and
/// which the consumer handles by being idempotent.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { } // EF Core

    private OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Routing key, e.g. <c>reservation.confirmed</c>.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>The serialised event body.</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Set once the message has failed too many times. It stops being retried and stays in
    /// the table — deleting it would destroy the only record that something was lost.
    /// </summary>
    public bool IsDead { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage(Guid.NewGuid(), type, payload, occurredAt);
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        LastError = null;
        NextAttemptAt = null;
    }

    /// <summary>
    /// Records a failed publish and schedules the retry.
    ///
    /// Backoff is exponential and capped. A broker that is down comes back; hammering it
    /// every second in the meantime just adds load to something already struggling.
    /// </summary>
    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        Attempts++;
        LastError = Truncate(error, 2000);

        if (Attempts >= maxAttempts)
        {
            IsDead = true;
            NextAttemptAt = null;
            return;
        }

        var delayTicks = Math.Min(baseDelay.Ticks * (1L << Math.Min(Attempts - 1, 20)), maxDelay.Ticks);
        NextAttemptAt = now.AddTicks(delayTicks);
    }

    public bool IsDue(DateTimeOffset now)
        => ProcessedAt is null && !IsDead && (NextAttemptAt is null || NextAttemptAt <= now);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
