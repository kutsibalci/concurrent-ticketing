using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;

namespace SeatReservation.Api.BackgroundServices;

/// <summary>
/// Releases seats whose hold has lapsed.
///
/// Without this, an abandoned checkout holds its seats forever and the event sells out
/// to nobody. The sweeper is a cleanup, not the rule: confirmation checks the deadline
/// itself, so a late sweep cannot let an expired hold through.
/// </summary>
public sealed class ExpiredHoldSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReservationOptions _options;
    private readonly ILogger<ExpiredHoldSweeper> _logger;

    public ExpiredHoldSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<ReservationOptions> options,
        ILogger<ExpiredHoldSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Rezervasyon süpürücüsü başladı, aralık: {Interval}.", _options.SweepInterval);

        using var timer = new PeriodicTimer(_options.SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var reservations = scope.ServiceProvider.GetRequiredService<ReservationService>();

                await reservations.ExpireLapsedHoldsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Caught and logged rather than allowed to propagate: an unhandled
                // exception here kills the BackgroundService for the lifetime of the
                // process, and holds would silently stop being reclaimed.
                _logger.LogError(ex, "Süpürme turu başarısız oldu; sonraki turda yeniden denenecek.");
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

        _logger.LogInformation("Rezervasyon süpürücüsü durdu.");
    }
}
