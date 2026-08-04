using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using SeatReservation.Application.Options;

namespace SeatReservation.Infrastructure.Messaging;

/// <summary>
/// Owns the single AMQP connection and declares the topology.
///
/// One connection per process, opened lazily and shared: an AMQP connection is a TCP
/// connection with a heartbeat, and opening one per publish would cost more than the
/// publish. Channels are cheap and are not shared — they are not thread-safe.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    public const string DeadLetterExchange = "seatreservation.events.dlx";
    public const string DeadLetterQueue = "seatreservation.notifications.dead";

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IChannel> CreateChannelAsync(CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(ct);

        // Publisher confirms: without them a publish returns as soon as the bytes are
        // written to the socket, so the outbox would mark a message processed that the
        // broker never accepted.
        return await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            if (_connection is not null)
                await _connection.DisposeAsync();

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                // The client reconnects on its own; the dispatcher's retry handles what
                // slips through, so a broker restart does not need an application restart.
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync("seat-reservation-api", ct);
            _logger.LogInformation("RabbitMQ baglantisi kuruldu: {Host}:{Port}", _options.Host, _options.Port);

            await DeclareTopologyAsync(_connection, ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Declares the exchange, queue and dead-letter path.
    ///
    /// Declared by both publisher and consumer, and declaration is idempotent, so neither
    /// has to start first. A message the consumer rejects lands on the dead-letter queue
    /// rather than being requeued forever — a poison message that loops is worse than one
    /// that stops somewhere visible.
    /// </summary>
    private static async Task DeclareTopologyAsync(IConnection connection, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            "seatreservation.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, routingKey: string.Empty, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            "seatreservation.notifications",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange },
            cancellationToken: ct);

        // Topic binding: this consumer wants everything about reservations, and a new
        // event type under that prefix reaches it without a code change.
        await channel.QueueBindAsync(
            "seatreservation.notifications", "seatreservation.events", "reservation.*", cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();

        _gate.Dispose();
    }
}
