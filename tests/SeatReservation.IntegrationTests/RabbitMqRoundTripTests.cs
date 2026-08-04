using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SeatReservation.Application.Options;
using SeatReservation.Infrastructure.Messaging;
using Testcontainers.RabbitMq;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// Proves the transport actually carries a message, against a real broker.
///
/// Everything else about the outbox is tested with a fake publisher, which is the right
/// tool for driving failure paths but proves nothing about whether the exchange, the
/// binding and the properties are correct. This is the one test that does.
/// </summary>
public sealed class RabbitMqRoundTripTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .Build();

    private RabbitMqConnection _connection = null!;
    private RabbitMqOptions _options = null!;

    public async ValueTask InitializeAsync()
    {
        await _broker.StartAsync();

        var uri = new Uri(_broker.GetConnectionString());

        _options = new RabbitMqOptions
        {
            Host = uri.Host,
            Port = uri.Port,
            UserName = uri.UserInfo.Split(':')[0],
            Password = uri.UserInfo.Split(':')[1]
        };

        _connection = new RabbitMqConnection(
            Options.Create(_options), NullLogger<RabbitMqConnection>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task Yayinlanan_mesaj_kuyruga_ulasir_ve_ozellikleri_korunur()
    {
        var publisher = new RabbitMqEventPublisher(_connection, Options.Create(_options));

        var messageId = Guid.NewGuid().ToString();
        const string payload = """{"reservationId":"abc","seatLabels":["A1"]}""";

        await publisher.PublishAsync(messageId, "reservation.confirmed", payload);

        var received = await ConsumeOneAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(received);
        // The message id is what the consumer keys idempotency on; losing it in transit
        // would silently turn at-least-once into duplicate notifications.
        Assert.Equal(messageId, received.Value.MessageId);
        Assert.Equal("reservation.confirmed", received.Value.Type);
        Assert.Equal(payload, received.Value.Body);
    }

    [Fact]
    public async Task Yonlendirme_anahtari_reservation_onekiyle_eslesir()
    {
        var publisher = new RabbitMqEventPublisher(_connection, Options.Create(_options));

        // The queue binds "reservation.*", so an event type added later still arrives
        // without a change to the binding.
        await publisher.PublishAsync(Guid.NewGuid().ToString(), "reservation.expired", """{"x":1}""");

        var received = await ConsumeOneAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(received);
        Assert.Equal("reservation.expired", received.Value.Type);
    }

    private async Task<(string? MessageId, string? Type, string Body)?> ConsumeOneAsync(TimeSpan timeout)
    {
        await using var channel = await _connection.CreateChannelAsync();

        var completion = new TaskCompletionSource<(string?, string?, string)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, args) =>
        {
            completion.TrySetResult((
                args.BasicProperties.MessageId,
                args.BasicProperties.Type,
                Encoding.UTF8.GetString(args.Body.Span)));

            return Task.CompletedTask;
        };

        await channel.BasicConsumeAsync(_options.Queue, autoAck: true, consumer);

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await completion.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
