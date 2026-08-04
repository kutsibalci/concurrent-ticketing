using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Options;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Application.Services;

/// <summary>
/// Moves outbox rows to the broker.
///
/// Runs as a loop rather than being triggered by the write that produced the row: a
/// trigger would be lost if the process died between commit and publish, which is the
/// failure the outbox exists to remove.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly IApplicationDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IApplicationDbContext db,
        IEventPublisher publisher,
        TimeProvider clock,
        IOptions<OutboxOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _db = db;
        _publisher = publisher;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Publishes one batch. Returns how many rows were claimed.</summary>
    public Task<int> DispatchPendingAsync(CancellationToken ct = default)
        => _db.ExecuteInTransactionAsync(async token =>
        {
            var now = _clock.GetUtcNow();

            // Claimed with FOR UPDATE SKIP LOCKED, so a second dispatcher takes a
            // different batch instead of waiting behind this one.
            var due = await _db.ClaimDueOutboxMessagesAsync(now, _options.BatchSize, token);

            if (due.Count == 0)
                return 0;

            var published = 0;

            foreach (var message in due)
            {
                try
                {
                    // The message id becomes the broker's message id, which is what lets
                    // the consumer recognise a redelivery. Publishing inside the
                    // transaction holds the row locks for the duration — the cost of
                    // network I/O in a transaction, paid so that a crash mid-batch leaves
                    // the rows unclaimed rather than claimed and unpublished.
                    await _publisher.PublishAsync(message.Id.ToString(), message.Type, message.Payload, token);

                    message.MarkProcessed(now);
                    published++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    message.MarkFailed(
                        ex.Message, now, _options.MaxAttempts, _options.RetryBaseDelay, _options.RetryMaxDelay);

                    if (message.IsDead)
                    {
                        // Kept in the table rather than deleted: the row is now the only
                        // record that this event never reached anyone.
                        _logger.LogError(ex,
                            "Outbox mesaji {MessageId} ({Type}) {Attempts} denemeden sonra olu olarak isaretlendi.",
                            message.Id, message.Type, message.Attempts);
                    }
                    else
                    {
                        _logger.LogWarning(ex,
                            "Outbox mesaji {MessageId} gonderilemedi (deneme {Attempts}); {NextAttempt} tarihinde tekrar denenecek.",
                            message.Id, message.Attempts, message.NextAttemptAt);
                    }
                }
            }

            await _db.SaveChangesAsync(token);

            if (published > 0)
                _logger.LogInformation("{Published}/{Claimed} outbox mesaji yayinlandi.", published, due.Count);

            return due.Count;
        }, ct);

    /// <summary>Rows that will never be retried. Surfaced for a health check or an operator query.</summary>
    public Task<int> CountDeadAsync(CancellationToken ct = default)
        => _db.OutboxMessages.CountAsync(m => m.IsDead, ct);
}
