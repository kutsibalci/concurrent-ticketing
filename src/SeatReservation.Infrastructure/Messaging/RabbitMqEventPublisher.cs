using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Options;

namespace SeatReservation.Infrastructure.Messaging;

public sealed class RabbitMqEventPublisher : IEventPublisher
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;

    public RabbitMqEventPublisher(RabbitMqConnection connection, IOptions<RabbitMqOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async Task PublishAsync(
        string messageId, string type, string payload, CancellationToken cancellationToken = default)
    {
        // A channel per publish. Channels are not thread-safe, and the dispatcher may run
        // concurrently with itself; they are cheap enough that sharing one is not worth
        // the synchronisation.
        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        var properties = new BasicProperties
        {
            // Carried through to the consumer, which uses it to recognise a redelivery.
            MessageId = messageId,
            Type = type,
            ContentType = "application/json",
            // Survives a broker restart — pointless without a durable queue, which is why
            // both are declared that way.
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        // Throws if the broker does not confirm, which is what the dispatcher relies on to
        // decide between "processed" and "retry".
        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: type,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Used when RabbitMQ is switched off.
///
/// The outbox still records every event, so nothing is lost — the rows simply stay
/// unprocessed until a broker is configured. Lets the API run with only a database, which
/// is what the integration tests and a fresh clone need.
/// </summary>
public sealed class NoOpEventPublisher : IEventPublisher
{
    public Task PublishAsync(
        string messageId, string type, string payload, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
