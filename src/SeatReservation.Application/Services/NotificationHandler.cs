using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SeatReservation.Application.Abstractions;
using SeatReservation.Domain.Events;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Application.Services;

/// <summary>
/// Turns a reservation event into a notification.
///
/// Idempotency lives here rather than in the transport: the broker can and will redeliver,
/// and the outbox publishes at least once, so "have I already done this?" has to be
/// answered against the same database the work is recorded in.
/// </summary>
public sealed class NotificationHandler
{
    public const string ConsumerName = "notifications";

    private readonly IApplicationDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationHandler> _logger;

    public NotificationHandler(
        IApplicationDbContext db, TimeProvider clock, ILogger<NotificationHandler> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Handles one delivery.
    ///
    /// Returns <c>true</c> when the message can be acknowledged — which includes the case
    /// where it was already handled. Returns <c>false</c> only when the payload cannot be
    /// understood at all, so it goes to the dead-letter queue rather than being retried
    /// forever; a message that will never parse does not parse better the second time.
    /// </summary>
    public async Task<bool> HandleAsync(Guid messageId, string type, string payload, CancellationToken ct = default)
    {
        // Checked first as a cheap short-circuit; the primary key below is what actually
        // decides, because two deliveries can pass this check at the same moment.
        var alreadyHandled = await _db.ProcessedMessages
            .AnyAsync(m => m.Id == messageId && m.Consumer == ConsumerName, ct);

        if (alreadyHandled)
        {
            _logger.LogDebug("Mesaj {MessageId} zaten islenmis, atlaniyor.", messageId);
            return true;
        }

        var handled = type switch
        {
            "reservation.confirmed" => Handle<ReservationConfirmedEvent>(payload, Confirmed),
            "reservation.cancelled" => Handle<ReservationCancelledEvent>(payload, Cancelled),
            "reservation.expired" => Handle<ReservationExpiredEvent>(payload, Expired),
            _ => UnknownType(type)
        };

        if (!handled)
            return false;

        _db.ProcessedMessages.Add(
            ProcessedMessage.Create(messageId, ConsumerName, type, _clock.GetUtcNow()));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Another delivery of the same message committed first. The work is done and
            // the receipt exists; acknowledging is correct.
            _logger.LogInformation("Mesaj {MessageId} eszamanli olarak islenmis, onaylaniyor.", messageId);
        }

        return true;
    }

    private bool Handle<TEvent>(string payload, Action<TEvent> action)
    {
        TEvent? @event;
        try
        {
            @event = JsonSerializer.Deserialize<TEvent>(payload, OutboxWriter.SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Mesaj govdesi ayristirilamadi; olu mektup kuyruguna gonderiliyor.");
            return false;
        }

        if (@event is null)
        {
            _logger.LogError("Mesaj govdesi null olarak ayristirildi; olu mektup kuyruguna gonderiliyor.");
            return false;
        }

        action(@event);
        return true;
    }

    private bool UnknownType(string type)
    {
        // Acknowledged rather than dead-lettered: an unknown type is usually a newer
        // producer publishing something this consumer does not care about yet, and
        // filling the dead-letter queue with those hides the real failures.
        _logger.LogWarning("Bilinmeyen olay turu {Type}; yok sayiliyor.", type);
        return true;
    }

    // The notifications themselves are logged rather than sent. Wiring a real mail
    // provider would add an integration without adding anything to the delivery problem
    // this part of the project is about.

    private void Confirmed(ReservationConfirmedEvent e) => _logger.LogInformation(
        "BILDIRIM -> {Email}: '{EventName}' icin {SeatCount} koltuk onaylandi ({Seats}), toplam {Total:N2} TL.",
        e.UserEmail, e.EventName, e.SeatLabels.Count, string.Join(", ", e.SeatLabels), e.TotalPrice);

    private void Cancelled(ReservationCancelledEvent e) => _logger.LogInformation(
        "BILDIRIM -> {Email}: rezervasyonunuz iptal edildi, koltuklar serbest birakildi ({Seats}).",
        e.UserEmail, string.Join(", ", e.SeatLabels));

    private void Expired(ReservationExpiredEvent e) => _logger.LogInformation(
        "BILDIRIM -> {Email}: odeme suresi doldu, koltuklariniz serbest birakildi ({Seats}).",
        e.UserEmail, string.Join(", ", e.SeatLabels));

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
