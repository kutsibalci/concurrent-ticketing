using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;

namespace SeatReservation.Api.BackgroundServices;

/// <summary>
/// Drives <see cref="OutboxDispatcher"/> on a loop.
///
/// Polling rather than being signalled by the write, because a signal is lost if the
/// process dies between the commit and the notification — the exact failure the outbox
/// exists to remove. Polling is slower to react and cannot lose anything.
/// </summary>
public sealed class OutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox gonderici basladi, aralik: {Interval}, parti: {BatchSize}.",
            _options.PollInterval, _options.BatchSize);

        using var timer = new PeriodicTimer(_options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

                // Keep going while batches come back full: a backlog after a broker outage
                // should drain at once rather than one batch per poll interval.
                int claimed;
                do
                {
                    claimed = await dispatcher.DispatchPendingAsync(stoppingToken);
                }
                while (claimed >= _options.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose. An unhandled exception here kills the
                // BackgroundService for the life of the process, and events would stop
                // being published with nothing but this one log line to say so.
                _logger.LogError(ex, "Outbox gonderim turu basarisiz oldu; sonraki turda tekrar denenecek.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox gonderici durdu.");
    }
}
