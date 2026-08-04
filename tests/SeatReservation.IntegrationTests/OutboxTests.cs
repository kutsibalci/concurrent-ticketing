using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;
using SeatReservation.Domain.Entities;
using SeatReservation.Domain.Events;
using SeatReservation.Domain.Outbox;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>A publisher the test controls, so the dispatcher's failure paths can be driven deliberately.</summary>
internal sealed class FakeEventPublisher : IEventPublisher
{
    private readonly Func<string, bool> _shouldFail;

    public FakeEventPublisher(Func<string, bool>? shouldFail = null)
        => _shouldFail = shouldFail ?? (_ => false);

    public List<(string Id, string Type, string Payload)> Published { get; } = [];

    public Task PublishAsync(string messageId, string type, string payload, CancellationToken ct = default)
    {
        if (_shouldFail(messageId))
            throw new InvalidOperationException("broker unreachable");

        Published.Add((messageId, type, payload));
        return Task.CompletedTask;
    }
}

[Collection(nameof(PostgresCollection))]
public sealed class OutboxTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public OutboxTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OutboxDispatcher NewDispatcher(
        IApplicationDbContext db, IEventPublisher publisher, TimeProvider clock, OutboxOptions? options = null)
        => new(db, publisher, clock,
            Options.Create(options ?? new OutboxOptions { BatchSize = 50, MaxAttempts = 3 }),
            NullLogger<OutboxDispatcher>.Instance);

    private async Task<(Guid EventId, List<Guid> SeatIds)> SeedEventAsync(int seatCount = 3)
    {
        await using var db = _factory.CreateDbContext();

        var @event = Event.Create("Outbox Testi", "Salon", DateTimeOffset.UtcNow.AddDays(5));
        @event.AddSeatBlock("A", 1, seatCount, 200m);

        db.Events.Add(@event);
        await db.SaveChangesAsync();

        var ids = await db.Seats.Where(s => s.EventId == @event.Id)
            .OrderBy(s => s.Number).Select(s => s.Id).ToListAsync();

        return (@event.Id, ids);
    }

    // ------------------------------------------------------------- atomicity

    /// <summary>
    /// The point of the pattern: the event and the state change are one write. If they
    /// were separate, a crash between them would leave the two systems disagreeing with
    /// nothing in the data to say so.
    /// </summary>
    [Fact]
    public async Task Onay_ile_outbox_kaydi_ayni_islemde_yazilir()
    {
        var (eventId, seatIds) = await SeedEventAsync();

        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("outbox@ornek.test")).AccessToken);

        var created = await client.ReserveAsync(eventId, seatIds[0], seatIds[1]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        await client.PostAsync($"/api/reservations/{reservation.Id}/confirm", null);

        await using var db = _factory.CreateDbContext();
        var message = await db.OutboxMessages.SingleAsync(m => m.Type == "reservation.confirmed");

        Assert.Null(message.ProcessedAt);
        Assert.Equal(0, message.Attempts);

        var payload = JsonSerializer.Deserialize<ReservationConfirmedEvent>(
            message.Payload, OutboxWriter.SerializerOptions)!;

        Assert.Equal(reservation.Id, payload.ReservationId);
        Assert.Equal("outbox@ornek.test", payload.UserEmail);
        Assert.Equal(400m, payload.TotalPrice);
        Assert.Equal(["A1", "A2"], payload.SeatLabels);
    }

    [Fact]
    public async Task Basarisiz_onay_outbox_kaydi_birakmaz()
    {
        // Confirming an expired hold fails, and the event must not exist for something
        // that never happened.
        var (eventId, seatIds) = await SeedEventAsync();

        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("basarisiz@ornek.test")).AccessToken);

        var created = await client.ReserveAsync(eventId, seatIds[0]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        await using (var db = _factory.CreateDbContext())
        {
            // Push the hold into the past directly, then confirm.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE reservations SET "HoldExpiresAt" = now() - interval '1 hour' WHERE "Id" = {0}""",
                reservation.Id);
        }

        var confirm = await client.PostAsync($"/api/reservations/{reservation.Id}/confirm", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, confirm.StatusCode);

        await using var check = _factory.CreateDbContext();
        Assert.False(await check.OutboxMessages.AnyAsync(m => m.Type == "reservation.confirmed"));
    }

    [Fact]
    public async Task Iptal_outbox_kaydi_uretir()
    {
        var (eventId, seatIds) = await SeedEventAsync();

        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("iptalolay@ornek.test")).AccessToken);

        var created = await client.ReserveAsync(eventId, seatIds[0]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        await client.DeleteAsync($"/api/reservations/{reservation.Id}");

        await using var db = _factory.CreateDbContext();
        Assert.True(await db.OutboxMessages.AnyAsync(m => m.Type == "reservation.cancelled"));
    }

    // ------------------------------------------------------------ dispatching

    [Fact]
    public async Task Gonderici_bekleyen_mesajlari_yayinlar_ve_isaretler()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", now));
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.cancelled", """{"a":2}""", now));
            await seed.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        await using (var db = _factory.CreateDbContext())
        {
            var claimed = await NewDispatcher(db, publisher, TimeProvider.System).DispatchPendingAsync();
            Assert.Equal(2, claimed);
        }

        Assert.Equal(2, publisher.Published.Count);

        await using var check = _factory.CreateDbContext();
        Assert.Equal(2, await check.OutboxMessages.CountAsync(m => m.ProcessedAt != null));
    }

    [Fact]
    public async Task Islenmis_mesaj_ikinci_kez_yayinlanmaz()
    {
        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();

        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, publisher, TimeProvider.System).DispatchPendingAsync();

        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, publisher, TimeProvider.System).DispatchPendingAsync();

        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task Yayin_hatasi_mesaji_islenmemis_birakir_ve_yeniden_dener()
    {
        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        var failing = new FakeEventPublisher(shouldFail: _ => true);

        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, failing, TimeProvider.System).DispatchPendingAsync();

        await using var check = _factory.CreateDbContext();
        var message = await check.OutboxMessages.SingleAsync();

        Assert.Null(message.ProcessedAt);
        Assert.Equal(1, message.Attempts);
        Assert.False(message.IsDead);
        Assert.NotNull(message.NextAttemptAt);
        Assert.Contains("broker unreachable", message.LastError);
    }

    [Fact]
    public async Task Geri_cekilme_suresi_dolana_kadar_tekrar_denenmez()
    {
        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        var failing = new FakeEventPublisher(shouldFail: _ => true);
        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, failing, TimeProvider.System).DispatchPendingAsync();

        // Immediately afterwards the message is not due, so a healthy broker is not
        // hammered with a message that just failed.
        var publisher = new FakeEventPublisher();
        await using (var db = _factory.CreateDbContext())
        {
            var claimed = await NewDispatcher(db, publisher, TimeProvider.System).DispatchPendingAsync();
            Assert.Equal(0, claimed);
        }

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Ust_sinira_ulasan_mesaj_olu_isaretlenir_ve_silinmez()
    {
        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        var failing = new FakeEventPublisher(shouldFail: _ => true);
        var options = new OutboxOptions
        {
            BatchSize = 10,
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero
        };

        for (var i = 0; i < 3; i++)
        {
            await using var db = _factory.CreateDbContext();
            await NewDispatcher(db, failing, TimeProvider.System, options).DispatchPendingAsync();
        }

        await using var check = _factory.CreateDbContext();
        var message = await check.OutboxMessages.SingleAsync();

        Assert.True(message.IsDead);
        Assert.Equal(3, message.Attempts);
        // Kept, not deleted: the row is the only record that this event never reached anyone.
        Assert.Null(message.ProcessedAt);
    }

    [Fact]
    public async Task Olu_mesaj_tekrar_denenmez()
    {
        var options = new OutboxOptions { BatchSize = 10, MaxAttempts = 1, RetryBaseDelay = TimeSpan.Zero };

        await using (var seed = _factory.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.Create("reservation.confirmed", """{"a":1}""", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, new FakeEventPublisher(_ => true), TimeProvider.System, options).DispatchPendingAsync();

        var publisher = new FakeEventPublisher();
        await using (var db = _factory.CreateDbContext())
            Assert.Equal(0, await NewDispatcher(db, publisher, TimeProvider.System, options).DispatchPendingAsync());

        Assert.Empty(publisher.Published);
    }

    /// <summary>
    /// Two dispatchers running at once must not publish the same row twice — this is what
    /// FOR UPDATE SKIP LOCKED buys, and the reason a second instance can simply be started.
    /// </summary>
    [Fact]
    public async Task Es_zamanli_gondericiler_ayni_mesaji_iki_kez_yayinlamaz()
    {
        const int messageCount = 40;

        await using (var seed = _factory.CreateDbContext())
        {
            for (var i = 0; i < messageCount; i++)
                seed.OutboxMessages.Add(
                    OutboxMessage.Create("reservation.confirmed", $$"""{"i":{{i}}}""", DateTimeOffset.UtcNow));

            await seed.SaveChangesAsync();
        }

        var options = new OutboxOptions { BatchSize = 10, MaxAttempts = 3 };
        var publishers = Enumerable.Range(0, 4).Select(_ => new FakeEventPublisher()).ToList();

        using var gate = new SemaphoreSlim(0, publishers.Count);

        var runs = publishers.Select(async publisher =>
        {
            await gate.WaitAsync();

            // Each dispatcher drains until it finds nothing left to claim.
            while (true)
            {
                await using var db = _factory.CreateDbContext();
                if (await NewDispatcher(db, publisher, TimeProvider.System, options).DispatchPendingAsync() == 0)
                    break;
            }
        }).ToList();

        gate.Release(publishers.Count);
        await Task.WhenAll(runs);

        var allPublished = publishers.SelectMany(p => p.Published.Select(m => m.Id)).ToList();

        Assert.Equal(messageCount, allPublished.Count);
        Assert.Equal(messageCount, allPublished.Distinct().Count());

        await using var check = _factory.CreateDbContext();
        Assert.Equal(messageCount, await check.OutboxMessages.CountAsync(m => m.ProcessedAt != null));
    }

    [Fact]
    public async Task Mesajlar_olusma_sirasina_gore_yayinlanir()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        await using (var seed = _factory.CreateDbContext())
        {
            for (var i = 0; i < 5; i++)
                seed.OutboxMessages.Add(
                    OutboxMessage.Create("reservation.confirmed", $$"""{"i":{{i}}}""", start.AddSeconds(i)));

            await seed.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        await using (var db = _factory.CreateDbContext())
            await NewDispatcher(db, publisher, TimeProvider.System).DispatchPendingAsync();

        var order = publisher.Published.Select(p => JsonDocument.Parse(p.Payload).RootElement.GetProperty("i").GetInt32());
        Assert.Equal([0, 1, 2, 3, 4], order);
    }
}
