using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;
using SeatReservation.Infrastructure.Messaging;

namespace SeatReservation.Worker;

/// <summary>
/// Consumes reservation events and hands each one to <see cref="NotificationHandler"/>.
/// </summary>
public sealed class NotificationConsumer : BackgroundService
{
    private readonly RabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<NotificationConsumer> _logger;

    private IChannel? _channel;

    public NotificationConsumer(
        RabbitMqConnection connection,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<NotificationConsumer> logger)
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The broker may not be up yet, or may have gone away. Reconnect rather
                // than exit: a worker that stops on the first connection error needs a
                // human to restart it every time RabbitMQ is redeployed.
                _logger.LogError(ex, "Tuketici baglantisi koptu; 5 saniye sonra yeniden denenecek.");

                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        _channel = await _connection.CreateChannelAsync(ct);

        // One unacknowledged message at a time per consumer. Without this the broker
        // pushes the whole queue at once, so a crash loses everything in flight and a slow
        // consumer holds messages other instances could be handling.
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: _options.Queue,
            // Manual acknowledgement. With autoAck the broker considers a message
            // delivered the moment it is written to the socket, so a crash during
            // handling loses it silently.
            autoAck: false,
            consumer: consumer,
            cancellationToken: ct);

        _logger.LogInformation("'{Queue}' kuyrugu dinleniyor.", _options.Queue);

        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        var messageIdRaw = args.BasicProperties.MessageId;
        var type = args.BasicProperties.Type ?? args.RoutingKey;

        if (!Guid.TryParse(messageIdRaw, out var messageId))
        {
            _logger.LogError("Mesaj kimligi okunamadi ({MessageId}); olu mektup kuyruguna gonderiliyor.", messageIdRaw);
            await _channel!.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        var payload = Encoding.UTF8.GetString(args.Body.Span);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<NotificationHandler>();

            var handled = await handler.HandleAsync(messageId, type, payload);

            if (handled)
            {
                await _channel!.BasicAckAsync(args.DeliveryTag, multiple: false);
            }
            else
            {
                // requeue: false sends it to the dead-letter exchange. A payload that
                // cannot be parsed will not parse on the next attempt either, and
                // requeueing it produces an infinite loop at full speed.
                await _channel!.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false);
            }
        }
        catch (Exception ex)
        {
            // A transient failure — the database being briefly unavailable, say. Requeued
            // so another attempt can succeed.
            _logger.LogError(ex, "Mesaj {MessageId} islenemedi; kuyruga geri konuyor.", messageId);
            await _channel!.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
            await _channel.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}
