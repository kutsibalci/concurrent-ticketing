using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Application.Services;
using SeatReservation.Domain.Events;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// The consumer is the component that makes at-least-once delivery safe, so its
/// idempotency is the part worth testing hardest.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class NotificationHandlerTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public NotificationHandlerTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static NotificationHandler NewHandler(ApplicationDbContext db)
        => new(db, TimeProvider.System, NullLogger<NotificationHandler>.Instance);

    private static string ConfirmedPayload() => JsonSerializer.Serialize(
        new ReservationConfirmedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "musteri@ornek.test", "Konser", 500m, ["A1", "A2"], DateTimeOffset.UtcNow),
        OutboxWriter.SerializerOptions);

    [Fact]
    public async Task Mesaj_islenir_ve_makbuz_yazilir()
    {
        var messageId = Guid.NewGuid();

        await using var db = _factory.CreateDbContext();
        var handled = await NewHandler(db).HandleAsync(messageId, "reservation.confirmed", ConfirmedPayload());

        Assert.True(handled);

        await using var check = _factory.CreateDbContext();
        var receipt = await check.ProcessedMessages.SingleAsync();
        Assert.Equal(messageId, receipt.Id);
        Assert.Equal(NotificationHandler.ConsumerName, receipt.Consumer);
    }

    /// <summary>
    /// The outbox publishes at least once and RabbitMQ redelivers anything unacknowledged,
    /// so the same message will arrive twice. It must not produce two notifications.
    /// </summary>
    [Fact]
    public async Task Ayni_mesaj_ikinci_kez_islenmez()
    {
        var messageId = Guid.NewGuid();
        var payload = ConfirmedPayload();

        await using (var db = _factory.CreateDbContext())
            Assert.True(await NewHandler(db).HandleAsync(messageId, "reservation.confirmed", payload));

        await using (var db = _factory.CreateDbContext())
            Assert.True(await NewHandler(db).HandleAsync(messageId, "reservation.confirmed", payload));

        await using var check = _factory.CreateDbContext();
        Assert.Equal(1, await check.ProcessedMessages.CountAsync());
    }

    /// <summary>
    /// Two deliveries landing at once both pass the "already handled?" check, so the
    /// primary key has to be what actually decides. One insert wins, the other is caught
    /// and acknowledged.
    /// </summary>
    [Fact]
    public async Task Es_zamanli_iki_teslimat_tek_makbuz_birakir()
    {
        var messageId = Guid.NewGuid();
        var payload = ConfirmedPayload();

        using var gate = new SemaphoreSlim(0, 2);

        var deliveries = Enumerable.Range(0, 2).Select(async _ =>
        {
            await gate.WaitAsync();
            await using var db = _factory.CreateDbContext();
            return await NewHandler(db).HandleAsync(messageId, "reservation.confirmed", payload);
        }).ToList();

        gate.Release(2);
        var results = await Task.WhenAll(deliveries);

        // Both are acknowledged — a duplicate is not a failure.
        Assert.All(results, Assert.True);

        await using var check = _factory.CreateDbContext();
        Assert.Equal(1, await check.ProcessedMessages.CountAsync());
    }

    [Fact]
    public async Task Ayristirilmayan_govde_olu_mektuba_gonderilir()
    {
        await using var db = _factory.CreateDbContext();

        // false means "do not requeue" — a payload that cannot be parsed will not parse on
        // the next attempt either, and requeueing it loops forever.
        var handled = await NewHandler(db).HandleAsync(Guid.NewGuid(), "reservation.confirmed", "bu json degil");

        Assert.False(handled);
        Assert.Equal(0, await db.ProcessedMessages.CountAsync());
    }

    [Fact]
    public async Task Bilinmeyen_olay_turu_onaylanir_ama_kuyrugu_tikamaz()
    {
        await using var db = _factory.CreateDbContext();

        // Acknowledged rather than dead-lettered: usually a newer producer publishing
        // something this consumer does not handle yet.
        var handled = await NewHandler(db).HandleAsync(Guid.NewGuid(), "reservation.somethingelse", "{}");

        Assert.True(handled);
    }

    [Fact]
    public async Task Iptal_ve_suresi_dolma_olaylari_da_islenir()
    {
        var cancelled = JsonSerializer.Serialize(
            new ReservationCancelledEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "a@ornek.test", ["B3"], DateTimeOffset.UtcNow),
            OutboxWriter.SerializerOptions);

        var expired = JsonSerializer.Serialize(
            new ReservationExpiredEvent(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "b@ornek.test", ["C4"], DateTimeOffset.UtcNow),
            OutboxWriter.SerializerOptions);

        await using var db = _factory.CreateDbContext();
        var handler = NewHandler(db);

        Assert.True(await handler.HandleAsync(Guid.NewGuid(), "reservation.cancelled", cancelled));
        Assert.True(await handler.HandleAsync(Guid.NewGuid(), "reservation.expired", expired));

        Assert.Equal(2, await db.ProcessedMessages.CountAsync());
    }
}
