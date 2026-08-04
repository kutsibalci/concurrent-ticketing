namespace SeatReservation.Application.Options;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often the dispatcher looks for unpublished messages.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Rows claimed per pass. Bounded so one slow batch cannot hold a transaction — and
    /// therefore row locks — open for long.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>After this many failures a message is marked dead and stops being retried.</summary>
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = "seatreservation.events";
    public string Queue { get; set; } = "seatreservation.notifications";

    /// <summary>Empty disables publishing entirely; the outbox still records events.</summary>
    public bool Enabled { get; set; } = true;
}
